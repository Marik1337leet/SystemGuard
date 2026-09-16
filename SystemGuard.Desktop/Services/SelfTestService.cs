// Встроенный самотест: `SystemGuard.exe --selftest`.
// Проверяет ТО ЖЕ, что трогает телефон вне дома: relay-протокол,
// парсеры ссылок туннеля, команды RemoteActions, живой HTTP API на loopback.
// Отдельный xUnit-раннер на этом ПК душит Smart App Control (новые DLL без
// репутации), а основной exe уже доверен — поэтому тесты живут здесь и
// гоняются одной командой перед каждым релизом. Зеркало: SystemGuard.Tests.
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace SystemGuard.Desktop.Services;

public static class SelfTestService
{
    public static async Task<int> RunAsync()
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { }
        int pass = 0, fail = 0;
        void Check(bool ok, string name, string detail = "")
        {
            Console.WriteLine((ok ? "[PASS] " : "[FAIL] ") + name + (detail == "" ? "" : " :: " + detail));
            try { Console.Out.Flush(); } catch { }
            if (ok) pass++; else fail++;
        }

        // ── 1. Bot Relay протокол ──────────────────────────────────────
        try
        {
            var p = BotRelayProtocol.BuildRequestWithId("abc12345", "VoLuMe", " 70 ");
            var req = BotRelayProtocol.TryParseRequest(p);
            Check(req != null && req.Id == "abc12345" && req.Action == "volume" && req.Arg == "70",
                "relay/request roundtrip+normalize");
        }
        catch (Exception ex) { Check(false, "relay/request roundtrip+normalize", ex.Message); }

        Check(BotRelayProtocol.TryParseRequest(BotRelayProtocol.BuildRequest("status")) != null,
            "relay/random id parseable");
        Check(BotRelayProtocol.TryParseRequest(BotRelayProtocol.BuildRequest("status")) is not null &&
              BotRelayProtocol.TryParseRequest(BotRelayProtocol.BuildRequest("status")) is not null,
            "relay/ids unique (parse both)");
        // уникальность именно строк:
        Check(BotRelayProtocol.BuildRequest("status") != BotRelayProtocol.BuildRequest("status"),
            "relay/ids differ");

        string?[] broken = [null, "", "/status", "🤖SG:", "🤖SG:not-json",
            "{\"action\":\"status\"}", // без префикса — не пакет
            "🤖SG:{\"action\":\"status\"}", "🤖SG:{\"id\":\"abc12345\"}",
            "🤖SG:{\"id\":\"x\",\"action\":\"status\",\"arg\":\"\"}"];
        bool allRejected = true;
        foreach (var b in broken) allRejected &= BotRelayProtocol.TryParseRequest(b) == null;
        Check(allRejected, "relay/broken packets rejected");

        var uni = BotRelayProtocol.TryParseRequest(BotRelayProtocol.BuildRequestWithId("unicode01", "type", "Привет мир!"));
        Check(uni != null && uni.Arg == "Привет мир!", "relay/unicode arg");
        var sl = BotRelayProtocol.TryParseRequest(BotRelayProtocol.BuildRequestWithId("slash0001", "/CMD", "ipconfig"));
        Check(sl != null && sl.Action == "cmd", "relay/slash normalized");
        var reply = BotRelayProtocol.FormatReply("volume", true, "Volume: 70%", "Volume: 70%");
        Check(!reply.Contains("SG-RESP") && reply.Contains("Volume: 70%"), "relay/human reply no packets");
        var replyFail = BotRelayProtocol.FormatReply("cmd", false, "Admins only", "");
        Check(replyFail.StartsWith("Error:") && replyFail.Contains("Admins only"), "relay/human fail prefixed");
        var bigReply = BotRelayProtocol.FormatReply("cmd", true, new string('x', 9000), new string('x', 9000));
        Check(bigReply.Length <= 4001, "relay/long output truncated", bigReply.Length + " chars");
        Check(BotRelayProtocol.IsReadOnlyAction("status") && !BotRelayProtocol.IsReadOnlyAction("cmd")
              && !BotRelayProtocol.IsReadOnlyAction("shutdown") && BotRelayProtocol.IsReadOnlyAction("uptime"),
            "relay/readonly matrix");
        Check(BotRelayProtocol.IsRelayRequest("🤖SG:{}") && !BotRelayProtocol.IsRelayRequest("/status"),
            "relay/prefix detection");

        // ── 1b. WebApp sendData → команда (fallback при мёртвом Live) ───
        Check(TelegramBotService.TryParseWebAppData("{\"action\":\"volume\",\"arg\":\"70\"}", out var waA, out var waG)
              && waA == "volume" && waG == "70", "webapp/json parsed");
        Check(TelegramBotService.TryParseWebAppData("{\"action\":\"dashboard\"}", out var waD, out _)
              && waD == "status", "webapp/dashboard alias");
        Check(TelegramBotService.TryParseWebAppData("{\"action\":\"pause\"}", out var waP, out _)
              && waP == "play", "webapp/pause alias");
        Check(!TelegramBotService.TryParseWebAppData("{}", out _, out _)
              && !TelegramBotService.TryParseWebAppData("", out _, out _), "webapp/broken rejected");

        // ── 2. Парсеры ссылок туннеля ──────────────────────────────────
        Check(TunnelService.PickLhrUrl("Forwarding https://abc-def-1-2-3-4.lhr.life -> 127.0.0.1:8899")
              == "https://abc-def-1-2-3-4.lhr.life", "tunnel/lhr simple");
        Check(TunnelService.PickLhrUrl("https://admin.localhost.run status") == null, "tunnel/lhr admin rejected");
        Check(TunnelService.PickLhrUrl("url: https://nice-name.lhr.rocks")
              == "https://nice-name.lhr.rocks", "tunnel/lhr.rocks");
        Check(TunnelService.PickPinggyUrl("access at https://abc-def.a.free.pinggy.link")
              == "https://abc-def.a.free.pinggy.link", "tunnel/pinggy simple");
        Check(TunnelService.PickPinggyUrl("dash https://dashboard.pinggy.io tun https://qwerty.a.free.pinggy.link")
              == "https://qwerty.a.free.pinggy.link", "tunnel/pinggy dashboard ignored");
        Check(TunnelService.PickPinggyUrl("open https://dashboard.pinggy.io") == null, "tunnel/pinggy only-dashboard null");
        // Живые форматы pinggy из стенда 13.09 (serveo мёртв — удалён):
        Check(TunnelService.PickPinggyUrl("https://mihuu-162-19-235-118.free.pinggy.net")
              == "https://mihuu-162-19-235-118.free.pinggy.net", "tunnel/pinggy net domain");
        Check(TunnelService.PickPinggyUrl("https://uypwf-162-19-235-118.run.pinggy-free.link")
              == "https://uypwf-162-19-235-118.run.pinggy-free.link", "tunnel/pinggy free-link domain");
        Check(TunnelService.PickLhrUrl("tunneled with tls termination, https://dc872105c69f64.lhr.life")
              == "https://dc872105c69f64.lhr.life", "tunnel/lhr live format");
        Check(TunnelService.TryPickTunnelUrl("see https://dashboard.pinggy.io and https://dc872105c69f64.lhr.life live")
              == "https://dc872105c69f64.lhr.life", "tunnel/trypick lhr over dashboard");
        Check(TunnelService.TryPickTunnelUrl("") == null
              && TunnelService.TryPickTunnelUrl("no urls (ssh: connected)") == null, "tunnel/trypick empty null");

        // ── 3. Разбор команд бота ──────────────────────────────────────
        Check(TelegramBotService.ExtractCommand("/ls C:\\Games") == "/ls", "bot/cmd ls");
        Check(TelegramBotService.ExtractCommand("/start@MyBot") == "/start", "bot/cmd mention stripped");
        Check(TelegramBotService.ExtractArg("/ls C:\\Games") == "C:\\Games", "bot/arg path");
        Check(TelegramBotService.ExtractArg("/status") == "", "bot/arg empty");

        // ── 4. RemoteActions (только безопасное: read-only + validation) ─
        async Task CheckAction(string action, string arg, bool expectOk, string name, string mustContain = "")
        {
            try
            {
                var r = await RemoteActions.ExecuteAsync(action, arg).ConfigureAwait(false);
                var json = JsonSerializer.Serialize(r);
                using var doc = JsonDocument.Parse(json);
                bool ok = !doc.RootElement.TryGetProperty("ok", out var o) || o.ValueKind == JsonValueKind.True;
                bool good = ok == expectOk && (mustContain == "" || json.Contains(mustContain));
                Check(good, "actions/" + name, Trim(json, 120));
            }
            catch (Exception ex) { Check(false, "actions/" + name, ex.Message); }
        }

        await CheckAction("status", "", true, "status ok", "machine").ConfigureAwait(false);
        await CheckAction("no_such_action_xyz", "", false, "unknown fails", "no_such_action_xyz").ConfigureAwait(false);
        await CheckAction("/uptime", "", true, "slash normalized").ConfigureAwait(false);
        await CheckAction("uptime", "", true, "uptime").ConfigureAwait(false);
        await CheckAction("battery", "", true, "battery").ConfigureAwait(false);
        await CheckAction("free", "", true, "free").ConfigureAwait(false);
        await CheckAction("perf", "", true, "perf").ConfigureAwait(false);
        await CheckAction("sysinfo", "", true, "sysinfo").ConfigureAwait(false);
        await CheckAction("processes", "", true, "processes items", "items").ConfigureAwait(false);
        await CheckAction("remote", "", true, "remote hub status", "AnyDesk").ConfigureAwait(false);
        await CheckAction("rdp", "", true, "rdp state").ConfigureAwait(false);
        await CheckAction("remote_install", "nope", false, "remote_install unknown fails").ConfigureAwait(false);
        await CheckAction("volume", "blah-blah", false, "volume garbage fails safe", "0-100").ConfigureAwait(false);
        await CheckAction("brightness", "blah", false, "brightness garbage fails").ConfigureAwait(false);
        await CheckAction("mouse_move", "blah", false, "mouse garbage fails").ConfigureAwait(false);
        await CheckAction("mouse_move_to", "0.5", false, "moveto garbage fails").ConfigureAwait(false);
        await CheckAction("key", "", false, "key empty fails").ConfigureAwait(false);
        await CheckAction("type", "   ", false, "type empty fails").ConfigureAwait(false);
        await CheckAction("wol", "", false, "wol empty fails", "MAC").ConfigureAwait(false);
        await CheckAction("unlock", "", false, "unlock empty fails").ConfigureAwait(false);
        await CheckAction("cmd", "   ", false, "cmd empty fails").ConfigureAwait(false);
        await CheckAction("ls", "Z:\\definitely\\not\\here\\sgtest", false, "ls missing fails").ConfigureAwait(false);

        // ── 5. HTTP API как телефон (loopback, тот же ?token=) ──────────
        const string token = "selftest-token-0123456789abcdef";
        int port;
        using (var l = new TcpListener(IPAddress.Loopback, 0)) { l.Start(); port = ((IPEndPoint)l.LocalEndpoint).Port; }
        var server = new RemoteHttpServer(token, port);
        bool serverUp = false;
        try { server.Start(); serverUp = true; }
        catch (Exception ex) { Check(false, "http/starts", ex.Message); }
        Check(serverUp && server.IsRunning, "http/starts on loopback");
        if (serverUp)
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var smiled = $"http://127.0.0.1:{port}";

        async Task<(bool Ok, string Body, int Status)> Get(string path)
        {
            try
            {
                using var r = await http.GetAsync(smiled + path).ConfigureAwait(false);
                return (r.IsSuccessStatusCode, await r.Content.ReadAsStringAsync().ConfigureAwait(false), (int)r.StatusCode);
            }
            catch (Exception ex) { return (false, ex.Message, -1); }
        }

        var api = await Get("/api").ConfigureAwait(false);
        Check(api.Ok && api.Body.Contains("\"ok\""), "http/GET /api open", $"HTTP {api.Status}");
        var noTok = await Get("/api/status");
        Check(!noTok.Ok && noTok.Status == 401, "http/no token 401", $"HTTP {noTok.Status}");
        var badTok = await Get("/api/status?token=wrong");
        Check(!badTok.Ok && badTok.Status == 401, "http/bad token 401", $"HTTP {badTok.Status}");
        var st = await Get("/api/status?token=" + Uri.EscapeDataString(token));
        Check(st.Ok && st.Body.Contains("machine"), "http/status?token= as phone", $"HTTP {st.Status}");

        // X-Token header
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, smiled + "/api/status");
            req.Headers.Add("X-Token", token);
            using var r = await http.SendAsync(req).ConfigureAwait(false);
            Check(r.IsSuccessStatusCode, "http/X-Token header", $"HTTP {(int)r.StatusCode}");
        }
        catch (Exception ex) { Check(false, "http/X-Token header", ex.Message); }

        // POST action JSON + text/plain (как WebApp без preflight)
        foreach (var ct in new[] { "application/json", "text/plain" })
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post,
                    smiled + "/api/action?token=" + Uri.EscapeDataString(token));
                req.Content = new StringContent(JsonSerializer.Serialize(new { action = "uptime", arg = "" }),
                    Encoding.UTF8, ct);
                using var r = await http.SendAsync(req).ConfigureAwait(false);
                var b = await r.Content.ReadAsStringAsync().ConfigureAwait(false);
                Check(r.IsSuccessStatusCode && b.Contains("\"ok\""), $"http/POST action as {ct}", $"HTTP {(int)r.StatusCode}");
            }
            catch (Exception ex) { Check(false, $"http/POST action as {ct}", ex.Message); }
        }

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post,
                smiled + "/api/action?token=" + Uri.EscapeDataString(token));
            req.Content = new StringContent(JsonSerializer.Serialize(new { action = "no_such_action_xyz", arg = "" }),
                Encoding.UTF8, "application/json");
            using var r = await http.SendAsync(req).ConfigureAwait(false);
            var b = await r.Content.ReadAsStringAsync().ConfigureAwait(false);
            Check(r.IsSuccessStatusCode && b.Contains("Unknown action"), "http/unknown action ok=false,not 500");
        }
        catch (Exception ex) { Check(false, "http/unknown action ok=false,not 500", ex.Message); }

        var getAction = await Get("/api/action?token=" + Uri.EscapeDataString(token));
        Check(!getAction.Ok && getAction.Status == 405, "http/GET action 405", $"HTTP {getAction.Status}");

        try
        {
            using var r = await http.GetAsync(smiled + "/api/status?token=" + Uri.EscapeDataString(token)).ConfigureAwait(false);
            Check(r.Headers.Contains("Access-Control-Allow-Origin"), "http/CORS header");
        }
        catch (Exception ex) { Check(false, "http/CORS header", ex.Message); }

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Options, smiled + "/api/action?token=" + Uri.EscapeDataString(token));
            using var r = await http.SendAsync(req).ConfigureAwait(false);
            Check((int)r.StatusCode == 204, "http/OPTIONS 204", $"HTTP {(int)r.StatusCode}");
        }
        catch (Exception ex) { Check(false, "http/OPTIONS 204", ex.Message); }

        var pol = await Get("/api/policy?kind=safety&token=" + Uri.EscapeDataString(token));
        Check(pol.Ok && pol.Body.Contains("safety"), "http/policy");
        var unk = await Get("/api/nope?token=" + Uri.EscapeDataString(token));
        Check(!unk.Ok && unk.Status == 404, "http/unknown 404", $"HTTP {unk.Status}");
            try { server.Dispose(); } catch { }
        }

        Console.WriteLine(fail == 0 ? $"ALL GREEN ({pass} passed)" : $"{fail} FAILURES ({pass} passed)");
        return fail == 0 ? 0 : 1;
    }

    private static string Trim(string? s, int n) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s[..n] + "…");
}
