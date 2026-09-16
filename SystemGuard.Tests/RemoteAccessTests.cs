// Хаб стороннего удалённого доступа: только безопасное —
// чистые парсеры, каталог, детект без исключений, read-only статус.
using SystemGuard.Desktop.Services;

namespace SystemGuard.Tests;

public sealed class RemoteAccessTests
{
    [Theory]
    [InlineData("123456789", "123456789")]
    [InlineData("  987 654 321 \n", "987654321")]
    [InlineData("Ad 1 234 567", "1234567")]
    public void AnyDesk_Id_Parsed(string raw, string expected)
    {
        Assert.Equal(expected, RemoteAccessService.ParseAnyDeskIdOutput(raw));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("12345")]
    public void AnyDesk_Id_Broken_Rejected(string? raw)
    {
        Assert.Null(RemoteAccessService.ParseAnyDeskIdOutput(raw));
    }

    [Fact]
    public void RustDesk_Id_From_Toml()
    {
        const string toml = "[options]\n  rendezvous_server = 'x'\nid = '987654321'\npassword = 'secret'\n";
        Assert.Equal("987654321", RemoteAccessService.ParseRustDeskIdFromToml(toml));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("[options]\nfoo = 1\n")]
    [InlineData("id = 123\n")]
    public void RustDesk_Id_Broken_Rejected(string? toml)
    {
        Assert.Null(RemoteAccessService.ParseRustDeskIdFromToml(toml));
    }

    [Theory]
    [InlineData("Professional", true)]
    [InlineData("Enterprise", true)]
    [InlineData("Education", true)]
    [InlineData("Core", false)]
    [InlineData("CoreSingleLanguage", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void Rdp_Only_Pro_Plus(string? edition, bool expected)
    {
        Assert.Equal(expected, RemoteAccessService.IsRdpCapableEdition(edition));
    }

    [Fact]
    public void Catalog_Complete_And_Https()
    {
        foreach (var key in new[] { "anydesk", "rustdesk", "teamviewer", "crd", "obs", "droidcam" })
        {
            Assert.True(RemoteAccessService.Catalog.ContainsKey(key), key);
            var item = RemoteAccessService.Catalog[key];
            Assert.False(string.IsNullOrWhiteSpace(item.Name));
            Assert.False(string.IsNullOrWhiteSpace(item.SilentArgs));
            Assert.True(item.DirectUrl.StartsWith("https://") || item.DirectUrl.StartsWith("github:"),
                $"{key}: {item.DirectUrl}");
        }
    }

    [Fact]
    public void DetectAll_No_Throw_And_Has_Rdp()
    {
        var all = RemoteAccessService.DetectAll();
        Assert.True(all.Count >= 7);
        Assert.Contains(all, t => t.Key == "rdp");
        Assert.Contains(all, t => t.Key == "anydesk");
        foreach (var t in all)
        {
            Assert.False(string.IsNullOrWhiteSpace(t.Name));
            Assert.False(string.IsNullOrWhiteSpace(t.Detail));
        }
    }

    [Fact]
    public async Task Remote_Status_Action_Ok()
    {
        var r = await RemoteActions.ExecuteAsync("remote", "");
        var json = System.Text.Json.JsonSerializer.Serialize(r);
        Assert.Contains("\"ok\":true", json);
        Assert.Contains("AnyDesk", json);
    }

    [Fact]
    public async Task Rdp_State_On_Home_Refuses_Honestly()
    {
        // На этой машине Home — RDP-хост невозможен, должен честно отказать.
        // Сравнение по декодированному полю: System.Text.Json пишет "+" как \u002B.
        if (!RemoteAccessService.IsRdpCapableEdition(RemoteAccessService.GetEditionId()))
        {
            var r = await RemoteActions.ExecuteAsync("rdp_on", "");
            using var doc = System.Text.Json.JsonDocument.Parse(
                System.Text.Json.JsonSerializer.Serialize(r));
            var msg = doc.RootElement.GetProperty("message").GetString() ?? "";
            Assert.Contains("Pro+", msg);
            Assert.Contains("RDP", msg);
        }
    }

    [Fact]
    public async Task Remote_Install_Unknown_Tool_Fails()
    {
        var r = await RemoteActions.ExecuteAsync("remote_install", "nope");
        var json = System.Text.Json.JsonSerializer.Serialize(r);
        Assert.Contains("\"ok\":false", json);
    }

    [Fact]
    public void Relay_Matrix_Remote_ReadOnly()
    {
        Assert.True(BotRelayProtocol.IsReadOnlyAction("remote"));
        Assert.True(BotRelayProtocol.IsReadOnlyAction("rdp"));
        Assert.False(BotRelayProtocol.IsReadOnlyAction("rdp_on"));
        Assert.False(BotRelayProtocol.IsReadOnlyAction("remote_install"));
    }
}
