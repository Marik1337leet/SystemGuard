// Парсеры ссылок туннеля: телефон хранит URL, провайдеры печатают его
// в разном мусоре (особенно pinggy TUI с ANSI). Поломка парсера = "не работает".
using SystemGuard.Desktop.Services;

namespace SystemGuard.Tests;

public sealed class TunnelParserTests
{
    [Fact]
    public void Lhr_Simple()
    {
        Assert.Equal("https://abc-def-1-2-3-4.lhr.life",
            TunnelService.PickLhrUrl("Forwarding https://abc-def-1-2-3-4.lhr.life -> 127.0.0.1:8899"));
    }

    [Fact]
    public void Lhr_Admin_Rejected()
    {
        Assert.Null(TunnelService.PickLhrUrl("https://admin.localhost.run status page"));
    }

    [Fact]
    public void Lhr_Rocks_Domain()
    {
        Assert.Equal("https://nice-name.lhr.rocks",
            TunnelService.PickLhrUrl("your url: https://nice-name.lhr.rocks"));
    }

    [Fact]
    public void Pinggy_Simple()
    {
        Assert.Equal("https://abc-def-ghi-jkl.a.free.pinggy.link",
            TunnelService.PickPinggyUrl("You can access local server at https://abc-def-ghi-jkl.a.free.pinggy.link"));
    }

    [Fact]
    public void Pinggy_Dashboard_Ignored()
    {
        // В TUI рядом лежит ссылка на дашборд — брать надо туннель, не её.
        var line = "dashboard https://dashboard.pinggy.io tunnel https://qwerty.a.free.pinggy.link end";
        Assert.Equal("https://qwerty.a.free.pinggy.link", TunnelService.PickPinggyUrl(line));
    }

    [Fact]
    public void Pinggy_Only_Dashboard_Null()
    {
        Assert.Null(TunnelService.PickPinggyUrl("open https://dashboard.pinggy.io to manage"));
    }

    // serveo выкинут из цепочки 13.09.2026 (Permission denied и с ключом,
    // и без) — парсера больше нет, вместо него живые форматы pinggy:
    [Fact]
    public void Pinggy_Net_Domain()
    {
        Assert.Equal("https://mihuu-162-19-235-118.free.pinggy.net",
            TunnelService.PickPinggyUrl("https://mihuu-162-19-235-118.free.pinggy.net"));
    }

    [Fact]
    public void Pinggy_Free_Link_Domain()
    {
        Assert.Equal("https://uypwf-162-19-235-118.run.pinggy-free.link",
            TunnelService.PickPinggyUrl("https://uypwf-162-19-235-118.run.pinggy-free.link"));
    }

    [Fact]
    public void TryPick_Lhr_Over_Pinggy_Dashboard()
    {
        var text = "see https://dashboard.pinggy.io and https://dc872105c69f64.lhr.life live";
        Assert.Equal("https://dc872105c69f64.lhr.life", TunnelService.TryPickTunnelUrl(text));
    }

    [Fact]
    public void TryPick_Empty_Null()
    {
        Assert.Null(TunnelService.TryPickTunnelUrl(""));
        Assert.Null(TunnelService.TryPickTunnelUrl("no urls here (ssh: connected)"));
    }

    [Fact]
    public void TryPick_Pinggy_Tui_Garbage()
    {
        // Кусок реального TUI pinggy с ANSI-перерисовками вокруг URL.
        var garbage = "\u001b[2J\u001b[HVisit https://dashboard.pinggy.io \u001b[K\n" +
                      "tunnel: https://ab-cd-12-34.a.free.pinggy.link, press ctrl+c";
        Assert.Equal("https://ab-cd-12-34.a.free.pinggy.link", TunnelService.TryPickTunnelUrl(garbage));
    }
}
