// Сквозной тест "как телефон": HttpClient бьёт в РЕАЛЬНЫЙ RemoteHttpServer
// на loopback (те же эндпоинты, тот же ?token=, тот же JSON).
// Туннель тут не нужен — он лишь пробрасывает этот же API наружу.
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using SystemGuard.Desktop.Services;

namespace SystemGuard.Tests;

public sealed class RemoteHttpServerTests : IAsyncDisposable
{
    private const string Token = "test-token-0123456789abcdef";
    private readonly RemoteHttpServer _server;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly string _base;

    public RemoteHttpServerTests()
    {
        var port = FreePort();
        _server = new RemoteHttpServer(Token, port);
        _server.Start();
        _base = $"http://127.0.0.1:{port}";
    }

    private static int FreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }

    public async ValueTask DisposeAsync()
    {
        try { _server.Dispose(); } catch { }
        _http.Dispose();
        await Task.CompletedTask;
    }

    [Fact]
    public void Server_Starts_On_Loopback()
    {
        Assert.True(_server.IsRunning);
    }

    [Fact]
    public async Task Api_Root_Open_Without_Token()
    {
        using var r = await _http.GetAsync(_base + "/api");
        var body = await r.Content.ReadAsStringAsync();
        Assert.True(r.IsSuccessStatusCode);
        Assert.Contains("\"ok\"", body);
    }

    [Fact]
    public async Task Status_Without_Token_401()
    {
        using var r = await _http.GetAsync(_base + "/api/status");
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Fact]
    public async Task Status_Bad_Token_401()
    {
        using var r = await _http.GetAsync(_base + "/api/status?token=wrong");
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Fact]
    public async Task Status_Query_Token_200_As_Phone()
    {
        // Телефон (<img>/fetch без headers) шлёт токен именно в query.
        using var r = await _http.GetAsync(_base + "/api/status?token=" + Uri.EscapeDataString(Token));
        var body = await r.Content.ReadAsStringAsync();
        Assert.True(r.IsSuccessStatusCode, body);
        Assert.Contains("machine", body);
    }

    [Fact]
    public async Task Status_Header_Token_200()
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, _base + "/api/status");
        req.Headers.Add("X-Token", Token);
        using var r = await _http.SendAsync(req);
        Assert.True(r.IsSuccessStatusCode);
    }

    [Fact]
    public async Task Action_Uptime_As_Json()
    {
        var payload = JsonSerializer.Serialize(new { action = "uptime", arg = "" });
        using var req = new HttpRequestMessage(HttpMethod.Post, _base + "/api/action?token=" + Uri.EscapeDataString(Token));
        req.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var r = await _http.SendAsync(req);
        var body = await r.Content.ReadAsStringAsync();
        Assert.True(r.IsSuccessStatusCode, body);
        Assert.Contains("\"ok\"", body);
    }

    [Fact]
    public async Task Action_As_Text_Plain_No_Preflight_As_WebApp()
    {
        // WebApp шлёт text/plain чтобы не ловить CORS-preflight через туннель.
        var payload = JsonSerializer.Serialize(new { action = "uptime", arg = "" });
        using var req = new HttpRequestMessage(HttpMethod.Post, _base + "/api/action?token=" + Uri.EscapeDataString(Token));
        req.Content = new StringContent(payload, Encoding.UTF8, "text/plain");
        using var r = await _http.SendAsync(req);
        var body = await r.Content.ReadAsStringAsync();
        Assert.True(r.IsSuccessStatusCode, body);
        Assert.Contains("\"ok\"", body);
    }

    [Fact]
    public async Task Action_Unknown_Returns_Ok_False_Not_500()
    {
        var payload = JsonSerializer.Serialize(new { action = "no_such_action_xyz", arg = "" });
        using var req = new HttpRequestMessage(HttpMethod.Post, _base + "/api/action?token=" + Uri.EscapeDataString(Token));
        req.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var r = await _http.SendAsync(req);
        var body = await r.Content.ReadAsStringAsync();
        Assert.True(r.IsSuccessStatusCode, body); // ошибка команды — не ошибка HTTP
        Assert.Contains("Unknown action", body);
    }

    [Fact]
    public async Task Action_Get_Method_Rejected_405()
    {
        using var r = await _http.GetAsync(_base + "/api/action?token=" + Uri.EscapeDataString(Token));
        Assert.Equal(HttpStatusCode.MethodNotAllowed, r.StatusCode);
    }

    [Fact]
    public async Task Cors_Headers_Present_For_Tunnel()
    {
        using var r = await _http.GetAsync(_base + "/api/status?token=" + Uri.EscapeDataString(Token));
        Assert.True(r.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task Options_Preflight_204()
    {
        using var req = new HttpRequestMessage(HttpMethod.Options, _base + "/api/action?token=" + Uri.EscapeDataString(Token));
        using var r = await _http.SendAsync(req);
        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
    }

    [Fact]
    public async Task Policy_Endpoint()
    {
        using var r = await _http.GetAsync(_base + "/api/policy?kind=safety&token=" + Uri.EscapeDataString(Token));
        var body = await r.Content.ReadAsStringAsync();
        Assert.True(r.IsSuccessStatusCode, body);
        Assert.Contains("safety", body);
    }

    [Fact]
    public async Task Unknown_Endpoint_404()
    {
        using var r = await _http.GetAsync(_base + "/api/nope?token=" + Uri.EscapeDataString(Token));
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }
}
