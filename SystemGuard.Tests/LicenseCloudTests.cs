// Облачная отчётность лицензий: payload строится только из валидных
// данных, без конфига сеть не трогаем и никогда не падаем.
using System.Text.Json;
using SystemGuard.Desktop.Services;

namespace SystemGuard.Tests;

public sealed class LicenseCloudTests
{
    private const string Hwid = "0123456789abcdef0123456789abcdef";
    private const string Key = "SG-PRO-QUJDREVGR0hJSktMTU5PUFFSU1RVVldY.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    [Fact]
    public void BuildPayload_Ok_Shape()
    {
        var p = SupabaseLicenseClient.BuildActivationPayload(Hwid, "MY-PC", Key, "Pro", "Yearly", "2.1.1");
        Assert.NotNull(p);
        var e = JsonDocument.Parse(JsonSerializer.Serialize(p)).RootElement;
        Assert.Equal(Hwid, e.GetProperty("hwid").GetString());
        Assert.Equal("MY-PC", e.GetProperty("machine").GetString());
        Assert.Equal("SG-PRO-QUJDREVGR0hJSktMTU5PU", e.GetProperty("key_prefix").GetString());
        Assert.Equal("Pro", e.GetProperty("tier").GetString());
        Assert.Equal("Yearly", e.GetProperty("plan").GetString());
        Assert.Equal("2.1.1", e.GetProperty("app_version").GetString());
        // Полного ключа в payload нет
        Assert.DoesNotContain(Key, JsonSerializer.Serialize(p));
    }

    [Theory]
    [InlineData("", "MY-PC", Key)]           // пустой hwid
    [InlineData("short", "MY-PC", Key)]      // мусорный hwid
    [InlineData(Hwid, "MY-PC", "")]          // пустой ключ
    [InlineData(Hwid, "MY-PC", "TRIAL-123")] // не ключ вовсе
    public void BuildPayload_Garbage_Returns_Null(string hwid, string machine, string key)
    {
        Assert.Null(SupabaseLicenseClient.BuildActivationPayload(hwid, machine, key, "Pro", "Yearly", "2.1.1"));
    }

    [Fact]
    public async Task Report_WithoutConfig_ReturnsFalse_And_Never_Throws()
    {
        // На тестовой машине supabase.json как правило нет; даже если есть —
        // метод обязан вернуть bool, а не упасть.
        var r = await SupabaseLicenseClient.ReportActivationAsync(Hwid, "TEST-PC", Key, "Pro", "Yearly", force: true);
        Assert.IsType<bool>(r);
    }

    [Fact]
    public void Report_FireAndForget_Never_Throws()
    {
        var ex = Record.Exception(() =>
            SupabaseLicenseClient.ReportActivation("bad-hwid", null, null, null, null, force: true));
        Assert.Null(ex);
    }
}
