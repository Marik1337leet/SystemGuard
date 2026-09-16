// Сквозной стенд живого доступа: гоняет РЕАЛЬНЫЙ production-код
// (RemoteHttpServer + TunnelService) от локального API до публичного URL.
// Запуск: SystemGuard.TunnelTest.exe (главное приложение должно быть ЗАКРЫТО —
// порт 8899 один на всех). Проверяет ровно то, что делает телефон в WebApp:
// те же эндпоинты, тот же ?token=, тот же text/plain POST без preflight.
using System.Net.Http;
using System.Text;
using System.Text.Json;
using SystemGuard.Desktop.Services;

try { Console.OutputEncoding = Encoding.UTF8; } catch { }

var failures = 0;
void Check(bool ok, string name, string detail = "")
{
    Console.WriteLine((ok ? "[PASS] " : "[FAIL] ") + name + (detail == "" ? "" : " :: " + detail));
    Console.Out.Flush();
    if (!ok) failures++;
}

const string token = "tunneltest-token-0123456789abcdef";
RemoteHttpServer? server = null;
try
{
    server = new RemoteHttpServer(token);
    server.Start();
    Check(server.IsRunning, "local API listens on :8899");
}
catch (Exception ex)
{
    Check(false, "local API listens on :8899", "Закройте главное приложение (порт занят): " + ex.Message);
    return 1;
}

using var tunnel = new TunnelService();
tunnel.Changed += () => Console.WriteLine($"  [tunnel] {tunnel.Status}");
var t0 = DateTime.UtcNow;
(bool Ok, string Message) res;
try
{
    using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(6));
    res = await tunnel.StartAsync(RemoteHttpServer.Port, cts.Token);
}
catch (Exception ex)
{
    Check(false, "tunnel published", ex.Message);
    server.Dispose();
    return 1;
}
Check(res.Ok && !string.IsNullOrEmpty(tunnel.PublicUrl),
    "tunnel published", $"{tunnel.Provider} in {(int)(DateTime.UtcNow - t0).TotalSeconds}s: {tunnel.PublicUrl}");
if (!res.Ok || string.IsNullOrEmpty(tunnel.PublicUrl))
{
    Console.WriteLine("--- TunnelLog tail ---");
    Console.WriteLine(tunnel.LastLog);
    tunnel.Stop();
    server.Dispose();
    return 1;
}
var baseUrl = tunnel.PublicUrl.TrimEnd('/');

using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
async Task<(bool Ok, string Body, int Status)> Get(string path)
{
    try
    {
        using var r = await http.GetAsync(baseUrl + path);
        var b = await r.Content.ReadAsStringAsync();
        return (r.IsSuccessStatusCode, b, (int)r.StatusCode);
    }
    catch (Exception ex) { return (false, ex.Message, -1); }
}

// 1. Открытый /api — как внешняя проверка доступности.
var api = await Get("/api");
Check(api.Ok && api.Body.Contains("\"ok\""), "GET /api from internet", $"HTTP {api.Status}: {Trim(api.Body, 120)}");

// 2. Статус с токеном в query (как WebApp, без кастомных headers).
var st = await Get("/api/status?token=" + Uri.EscapeDataString(token));
Check(st.Ok && st.Body.Contains("machine"), "GET /api/status?token=", $"HTTP {st.Status}: {Trim(st.Body, 120)}");

// 3. Чужой токен — 401.
var bad = await Get("/api/status?token=wrong");
Check(!bad.Ok && bad.Status == 401, "bad token rejected", $"HTTP {bad.Status}");

// 4. Команда POST text/plain (как WebApp после no-preflight фикса).
string postBody = "", postStatus = "";
try
{
    using var req = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/api/action?token=" + Uri.EscapeDataString(token));
    req.Content = new StringContent(JsonSerializer.Serialize(new { action = "uptime", arg = "" }), Encoding.UTF8, "text/plain");
    using var pr = await http.SendAsync(req);
    postStatus = ((int)pr.StatusCode).ToString();
    postBody = await pr.Content.ReadAsStringAsync();
}
catch (Exception ex) { postBody = ex.Message; }
Check(postBody.Contains("\"ok\""), "POST /api/action {uptime} as text/plain", $"HTTP {postStatus}: {Trim(postBody, 140)}");

// 5. Скриншот идёт (байты JPEG).
byte[]? shot = null;
try
{
    using var r = await http.GetAsync(baseUrl + "/api/shot.jpg?token=" + Uri.EscapeDataString(token) + "&w=640&q=50");
    if (r.IsSuccessStatusCode) shot = await r.Content.ReadAsByteArrayAsync();
}
catch { }
Check(shot is { Length: > 5000 }, "GET /api/shot.jpg bytes", shot == null ? "no bytes" : $"{shot.Length} bytes");

tunnel.Stop();
server.Dispose();
Console.WriteLine(failures == 0 ? "ALL GREEN" : $"{failures} FAILURES");
return failures == 0 ? 0 : 1;

static string Trim(string? s, int n) =>
    string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s[..n] + "…");
