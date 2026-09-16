// Протокол Bot Relay (Android ↔ ПК через Telegram) — то, что чинит
// "вне дома ничего не работает": команды идут без туннеля.
using SystemGuard.Desktop.Services;

namespace SystemGuard.Tests;

public sealed class BotRelayProtocolTests
{
    [Fact]
    public void Request_Roundtrip_Action_Arg()
    {
        var packet = BotRelayProtocol.BuildRequestWithId("abc12345", "VoLuMe", " 70 ");
        Assert.StartsWith(BotRelayProtocol.RequestPrefix, packet);
        var req = BotRelayProtocol.TryParseRequest(packet);
        Assert.NotNull(req);
        Assert.Equal("abc12345", req!.Id);
        Assert.Equal("volume", req.Action); // нормализация: lower + без /
        Assert.Equal("70", req.Arg);
    }

    [Fact]
    public void Request_Random_Id_Is_Unique_And_Parseable()
    {
        var a = BotRelayProtocol.BuildRequest("status");
        var b = BotRelayProtocol.BuildRequest("status");
        Assert.NotEqual(a, b);
        Assert.NotNull(BotRelayProtocol.TryParseRequest(a));
        Assert.NotNull(BotRelayProtocol.TryParseRequest(b));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/status")]
    [InlineData("🤖SG:")]
    [InlineData("🤖SG:not-json")]
    [InlineData("🤖SG:{\"action\":\"status\"}")] // нет id
    [InlineData("🤖SG:{\"id\":\"abc12345\"}")] // нет action
    [InlineData("🤖SG:{\"id\":\"x\",\"action\":\"status\",\"arg\":\"\"}")] // id короткий
    public void Request_Broken_Packets_Rejected(string? text)
    {
        Assert.Null(BotRelayProtocol.TryParseRequest(text));
    }

    [Fact]
    public void Request_Unicode_Arg_Survives()
    {
        var packet = BotRelayProtocol.BuildRequestWithId("unicode01", "type", "Привет мир!");
        var req = BotRelayProtocol.TryParseRequest(packet);
        Assert.NotNull(req);
        Assert.Equal("Привет мир!", req!.Arg);
    }

    [Fact]
    public void Request_Slashes_Normalized()
    {
        var packet = BotRelayProtocol.BuildRequestWithId("slash0001", "/CMD", "ipconfig");
        Assert.Equal("cmd", BotRelayProtocol.TryParseRequest(packet)!.Action);
    }

    [Fact]
    public void Response_Is_Human_Readable_No_Machine_Packets()
    {
        // Машинных SG-RESP пакетов больше нет осознанно: свои сообщения бот
        // через getUpdates не видит, парсить ответ в приложении невозможно.
        var text = BotRelayProtocol.FormatReply("volume", true, "Volume: 70%", "Volume: 70%");
        Assert.DoesNotContain("SG-RESP", text);
        Assert.Contains("Volume: 70%", text);
    }

    [Fact]
    public void Response_Fail_Prefixed_With_Error()
    {
        var text = BotRelayProtocol.FormatReply("cmd", false, "Admins only", "");
        Assert.StartsWith("Error:", text);
        Assert.Contains("Admins only", text);
    }

    [Fact]
    public void Response_Appends_Distinct_Output()
    {
        var text = BotRelayProtocol.FormatReply("uptime", true, "Uptime: 1 day", "Uptime: 1 day, boots: 3");
        Assert.Contains("Uptime: 1 day", text);
        Assert.Contains("boots: 3", text);
    }

    [Fact]
    public void Response_Long_Output_Truncated_For_Telegram_Limit()
    {
        var big = new string('x', 9000);
        var text = BotRelayProtocol.FormatReply("cmd", true, big, big);
        Assert.True(text.Length <= 4001); // влезет в лимит TG 4096
        Assert.EndsWith("…", text);
    }

    [Fact]
    public void Prefix_Detection()
    {
        Assert.True(BotRelayProtocol.IsRelayRequest("🤖SG:{}"));
        Assert.False(BotRelayProtocol.IsRelayRequest("/status"));
        Assert.False(BotRelayProtocol.IsRelayRequest(null));
    }

    [Theory]
    [InlineData("status", true)]
    [InlineData("perf", true)]
    [InlineData("processes", true)]
    [InlineData("uptime", true)]
    [InlineData("sysinfo", true)]
    [InlineData("remote", true)]
    [InlineData("rdp", true)]
    [InlineData("volume", false)]
    [InlineData("cmd", false)]
    [InlineData("shutdown", false)]
    [InlineData("unlock", false)]
    [InlineData("mouse_move", false)]
    [InlineData("type", false)]
    [InlineData("rdp_on", false)]
    [InlineData("remote_install", false)]
    public void ReadOnly_Matrix(string action, bool expected)
    {
        Assert.Equal(expected, BotRelayProtocol.IsReadOnlyAction(action));
    }

}
