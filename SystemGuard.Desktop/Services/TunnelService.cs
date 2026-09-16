using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SystemGuard.Desktop.Services;

// Публичный HTTPS-доступ к RemoteHttpServer через Cloudflare Quick Tunnel.
// Нужен, чтобы WebApp внутри Telegram (HTTPS-страница) мог дотянуться
// до ПК живым видеопотоком и мгновенными командами из любой сети.
// Токен всё равно проверяется на каждый запрос.
public sealed class TunnelService : IDisposable
{
    private static readonly HttpClient _dl = new() { Timeout = TimeSpan.FromMinutes(10) };
    private Process? _proc;
    private CancellationTokenSource? _cts;
    private bool _disposed;

    public bool IsRunning => _proc is { HasExited: false };
    public string? PublicUrl { get; private set; }
    // Активный транспорт: "SSH" (localhost.run) или "Cloudflare".
    public string? Provider { get; private set; }
    public string Status { get; private set; } = "Stopped";
    public string LastLog { get; private set; } = "";
    public event Action? Changed;
    private volatile bool _userStop;

    private void AppendLog(string line)
    {
        var lines = (LastLog + "\n" + line).Split('\n');
        LastLog = string.Join("\n", lines.TakeLast(12));
    }

    public static string BinaryPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SystemGuard", "bin", "cloudflared.exe");

    public static bool BinaryExists => File.Exists(BinaryPath);

    private static readonly SemaphoreSlim _dlGate = new(1, 1);

    public async Task<(bool Ok, string Message)> EnsureBinaryAsync(
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        // Одна загрузка за раз: параллельные Publish давали конфликт
        // "file is being used by another process" на tmp-файле.
        await _dlGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (BinaryExists) return (true, "cloudflared ready");
            const string url = "https://github.com/cloudflare/cloudflared/releases/latest/download/cloudflared-windows-amd64.exe";
            var dir = Path.GetDirectoryName(BinaryPath)!;
            Directory.CreateDirectory(dir);
            // Уникальный tmp + ретраи: свежий exe часто лочит антивирус при скане
            var tmp = BinaryPath + $".{Guid.NewGuid():N}.download";
            try
            {
                progress?.Report("Downloading cloudflared (~30 MB)…");
                using var res = await _dl.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                res.EnsureSuccessStatusCode();
                await using var net = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var fs = File.Open(tmp, FileMode.Create, FileAccess.Write, FileShare.None);
                await net.CopyToAsync(fs, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                try { File.Delete(tmp); } catch { }
                return (false, $"Tunnel binary download failed: {Trim(ex.Message, 160)}");
            }
            // Перемещение с ретраями (антивирус/индексатор могут держать файл)
            for (int i = 0; i < 6; i++)
            {
                try
                {
                    if (File.Exists(BinaryPath))
                    {
                        try { File.Delete(BinaryPath); }
                        catch { await Task.Delay(1000, ct).ConfigureAwait(false); continue; }
                    }
                    File.Move(tmp, BinaryPath);
                    progress?.Report("cloudflared ready");
                    return (true, "cloudflared ready");
                }
                catch when (i < 5)
                {
                    try { await Task.Delay(1500, ct).ConfigureAwait(false); } catch { }
                }
                catch (Exception ex)
                {
                    try { File.Delete(tmp); } catch { }
                    return (false, $"Tunnel binary install failed: {Trim(ex.Message, 160)}");
                }
            }
            try { File.Delete(tmp); } catch { }
            return (false, "Tunnel binary install failed: file is locked (antivirus?). Try again.");
        }
        finally { _dlGate.Release(); }
    }

    // Цепочка транспортов: сначала SSH (localhost.run через встроенный ssh.exe —
    // обычный TCP 443 наружу, без скачивания бинарников, без QUIC, без страниц
    // проверки), при неудаче — Cloudflare quick tunnel. Телефону без разницы:
    // там тот же https-URL + токен. После обрыва — автопереподключение
    // (URL при этом меняется, свежий всегда в чате по /live).
    public async Task<(bool Ok, string Message)> StartAsync(int localPort, CancellationToken ct = default)
    {
        if (IsRunning) return (true, PublicUrl ?? "already running");
        Stop();
        _userStop = false;
        _cts = new CancellationTokenSource();
        PublicUrl = null;
        Provider = null;
        LastLog = "";
        _localPort = localPort;
        Status = "Starting…";
        Changed?.Invoke();
        try
        {
            // 1) SSH-туннели — основной транспорт (цепочка внутри).
            var ssh = await StartSshAsync(localPort, _cts.Token).ConfigureAwait(false);
            if (ssh != null)
                return await AttachAsync(ssh.Value.Proc, ssh.Value.Url, "SSH (" + ssh.Value.Via + ")", _cts.Token).ConfigureAwait(false);
            if (_cts.Token.IsCancellationRequested || _userStop)
            {
                Stop();
                return (false, "Stopped");
            }
            // Cloudflare quick tunnel из цепочки УБРАН: проверено стендом —
            // cloudflared URL выдаёт, но случайный *.trycloudflare.com не
            // резолвится (DNS FAIL) и TCP 443 висит. Только время теряем.
            // Остаются SSH-туннели (TCP к фиксированным IP — работает).
            AppendLog("SSH tunnel unavailable (providers throttled? wait a few minutes and retry)");
            {
                var log = LastLog.Trim();
                Stop();
                Status = string.IsNullOrEmpty(log)
                    ? "Tunnel failed: SSH providers unreachable"
                    : "Tunnel failed. Log tail:\n" + log;
                Changed?.Invoke();
                return (false, Status);
            }
        }
        catch (Exception ex)
        {
            Stop();
            Status = $"Tunnel error: {Trim(ex.Message, 140)}";
            Changed?.Invoke();
            return (false, Status);
        }
    }

    private int _localPort;

    // Пока live опубликован — запрещаем ПК уходить в сон (иначе доступ
    // "вне дома" умрёт вместе с туннелем). ES_CONTINUOUS держится, пока
    // приложение запущено; Stop() снимает. Дисплей гаснуть может, система — нет.
    private static class SleepBlocker
    {
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern uint SetThreadExecutionState(uint esFlags);
        private const uint ES_CONTINUOUS = 0x80000000;
        private const uint ES_SYSTEM_REQUIRED = 0x00000001;
        public static void Prevent()
        {
            try { SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED); } catch { }
        }
        public static void Allow()
        {
            try { SetThreadExecutionState(ES_CONTINUOUS); } catch { }
        }
    }

    // Общая финализация: запоминаем URL, проверяем доступность снаружи,
    // запускаем сторожа с автопереподключением.
    private async Task<(bool Ok, string Message)> AttachAsync(Process proc, string url, string provider, CancellationToken ct)
    {
        _proc = proc;
        PublicUrl = url;
        Provider = provider;
        SleepBlocker.Prevent();
        Status = $"Live via {provider}: " + url;
        Changed?.Invoke();
        // Проверяем, что туннель реально отвечает СНАРУЖИ, а не только поднял
        // процесс: иначе на ПК "Live", а с телефона — тишина. /api открыт без токена.
        var probeErr = await ProbePublicAsync(url, ct).ConfigureAwait(false);
        if (probeErr != null)
        {
            AppendLog("remote check: " + probeErr + " — с телефона может не открыться, жмите Проверить в WebApp");
            Status = $"Live via {provider} (не проверен снаружи): " + url;
            Changed?.Invoke();
        }
        var token = ct;
        _ = Task.Run(() => MonitorAsync(provider, token), token);
        return (true, PublicUrl);
    }

    // Сторож: обрыв → пауза → подъём того же транспорта (до 5 попыток подряд,
    // дальше сдаёмся, чтобы не спамить процессами; счётчик сбрасывается успехом).
    private async Task MonitorAsync(string provider, CancellationToken ct)
    {
        int fails = 0;
        while (!ct.IsCancellationRequested && !_userStop)
        {
            try { var p = _proc; if (p == null) break; await p.WaitForExitAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch { break; }
            if (ct.IsCancellationRequested || _userStop) break;
            fails++;
            if (fails > 5)
            {
                PublicUrl = null; Provider = null;
                try { SleepBlocker.Allow(); } catch { }
                Status = "Tunnel died — press Publish live link again";
                Changed?.Invoke();
                break;
            }
            try
            {
                PublicUrl = null;
                Status = $"Reconnecting ({provider})… (попытка {fails})";
                Changed?.Invoke();
                // Пауза побольше: free-провайдеры душат частые переподключения.
                await Task.Delay(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
                // Переподключение — всегда по SSH-цепочке заново:
                // упавший провайдер пропустится сам, живой подхватит.
                var up3 = await StartSshAsync(_localPort, ct).ConfigureAwait(false);
                if (up3 == null) continue;
                fails = 0;
                _proc = up3.Value.Proc;
                PublicUrl = up3.Value.Url;
                Provider = "SSH (" + up3.Value.Via + ")";
                Status = $"Live via {Provider}: " + up3.Value.Url;
                Changed?.Invoke();
            }
            catch (OperationCanceledException) { break; }
            catch { /* следующая итерация */ }
        }
        try
        {
            if (_userStop || ct.IsCancellationRequested)
            {
                PublicUrl = null; Provider = null;
                Status = "Stopped";
                Changed?.Invoke();
            }
        }
        catch { }
    }

    // ── SSH-туннели ────────────────────────────────────────────────────
    // Встроенный в Windows ssh.exe, наружу — обычный TCP, без скачивания
    // бинарников и без аккаунтов. Два провайдера в цепочке (проверено
    // вживую 13.09.2026, оба выдают URL; serveo мёртв — Permission denied
    // и с ключом, и без — выкинут):
    //   • pinggy (порт 443): проходит почти везде, URL вида
    //     *.free.pinggy.net / *.run.pinggy-free.link. Требует НАШ ключ
    //     (-i, иначе Permission denied) и PTY (-tt, иначе TUI молчит
    //     и URL не появляется). Free живёт 60 минут (сторож переподключит).
    //   • localhost.run (22, запасной 443): долгая жизнь ссылки.
    //     Требует shell БЕЗ -N и строго БЕЗ -i ключа (ключ всё ломает).
    // Свой persistent ed25519-ключ — только для pinggy (иначе password-
    // prompt, который BatchMode убивает).
    private static readonly Regex LhrUrlRx =
        new(@"https://(?!admin\.)[A-Za-z0-9.-]+\.(lhr\.life|lhr\.rocks|localhost\.run)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex PinggyUrlRx =
        new(@"https://[A-Za-z0-9.-]+\.[A-Za-z]{2,}(?:\.[A-Za-z]{2,})?", RegexOptions.Compiled);

    private sealed record SshTarget(string Name, string SshArgs, Func<string, string?> PickUrl);

    private static bool SshAvailable()
    {
        try
        {
            using var p = new Process
            {
                StartInfo = new ProcessStartInfo("ssh", "-V")
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true
                }
            };
            p.Start();
            return p.WaitForExit(8000) && p.ExitCode == 0;
        }
        catch { return false; }
    }

    // Постоянный ключ %LocalAppData%\SystemGuard\ssh\tunnel_key (создаётся раз).
    private static string? EnsureSshKey()
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SystemGuard", "ssh");
            Directory.CreateDirectory(dir);
            var key = Path.Combine(dir, "tunnel_key");
            if (File.Exists(key) && File.Exists(key + ".pub")) return key;
            using var p = new Process
            {
                StartInfo = new ProcessStartInfo("ssh-keygen",
                    $"-t ed25519 -N \"\" -f \"{key}\" -q -C SystemGuard-tunnel")
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true
                }
            };
            p.Start();
            if (!p.WaitForExit(20000) || !File.Exists(key)) return null;
            return key;
        }
        catch { return null; }
    }

    public static string? PickLhrUrl(string line)
    {
        var m = LhrUrlRx.Match(line);
        return m.Success ? m.Value.TrimEnd('.', ',', ')') : null;
    }

    public static string? PickPinggyUrl(string line)
    {
        // В TUI есть и https://dashboard.pinggy.io — её отбрасываем.
        foreach (System.Text.RegularExpressions.Match m in PinggyUrlRx.Matches(line))
        {
            var u = m.Value.TrimEnd('.', ',', ')', '\'', '"');
            var host = u.Substring("https://".Length);
            if (host.Contains("pinggy", StringComparison.OrdinalIgnoreCase) &&
                !host.Contains("dashboard", StringComparison.OrdinalIgnoreCase))
                return u;
        }
        return null;
    }

    /// <summary>
    /// Общий парсер для тестов и диагностики: пробует все известные форматы.
    /// Порядок: lhr → pinggy (пингги самый мусорный — последним).
    /// </summary>
    public static string? TryPickTunnelUrl(string text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        return PickLhrUrl(text) ?? PickPinggyUrl(text);
    }

    private async Task<(Process Proc, string Url, string Via)?> StartSshAsync(int localPort, CancellationToken ct)
    {
        if (!SshAvailable())
        {
            AppendLog("ssh.exe not found (Windows OpenSSH missing?) — skipping SSH");
            return null;
        }
        var key = EnsureSshKey();
        var keyPart = key != null ? $"-o IdentitiesOnly=yes -i \"{key}\" " : "";
        // Ключ — НЕ общий: проверено вживую (стенд 13.09.2026) —
        //   • pinggy ТРЕБУЕТ наш ключ (без -i: Permission denied);
        //   • localhost.run ключ ЛОМАЕТ (с -i: permission denied, без: welcome-banner).
        var common = "-o StrictHostKeyChecking=no -o UserKnownHostsFile=NUL " +
                     "-o ServerAliveInterval=30 -o ServerAliveCountMax=3 " +
                     "-o ConnectTimeout=15 -o BatchMode=yes ";
        var targets = new[]
        {
            // pinggy:443 — первым: проходит почти везде, URL вида
            // *.free.pinggy.net / *.run.pinggy-free.link. Нужны И ключ, И PTY:
            // без -tt TUI не печатает URL ("Wait while we prepare the UI" —
            // и тишина). Минус: free живёт 60 минут (сторож переподключит
            // сам, ссылка новая — она автопушится в личку бота).
            new SshTarget("pinggy",
                $"{common}{keyPart}-tt -p 443 -R0:127.0.0.1:{localPort} free.pinggy.io",
                PickPinggyUrl),
            // localhost.run:22 — долгая жизнь ссылки. Только shell БЕЗ -N
            // (с -N URL не печатается) и строго БЕЗ -i ключа.
            new SshTarget("localhost.run",
                $"{common}-p 22 -R 80:127.0.0.1:{localPort} nokey@localhost.run",
                PickLhrUrl),
            // localhost.run:443 — на случай сетей с закрытым 22 (сервер слушает не всегда).
            new SshTarget("localhost.run:443",
                $"{common}-p 443 -R 80:127.0.0.1:{localPort} nokey@localhost.run",
                PickLhrUrl),
        };
        var waits = new[] { TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(35), TimeSpan.FromSeconds(25) };
        for (int i = 0; i < targets.Length; i++)
        {
            if (ct.IsCancellationRequested || _userStop) return null;
            if (i > 0)
            {
                // Пауза между провайдерами: free-сервисы душат очередь быстрых попыток.
                try { await Task.Delay(TimeSpan.FromSeconds(4), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return null; }
            }
            var t = targets[i];
            Status = $"Starting SSH tunnel via {t.Name}…";
            Changed?.Invoke();
            var up = await StartOneSshAsync(t, waits[i], ct).ConfigureAwait(false);
            if (up != null) return (up.Value.Proc, up.Value.Url, t.Name);
        }
        return null;
    }

    private async Task<(Process Proc, string Url)?> StartOneSshAsync(SshTarget t, TimeSpan wait, CancellationToken ct)
    {
        var proc = new Process
        {
            StartInfo = new ProcessStartInfo("ssh", t.SshArgs)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            }
        };
        string? url = null;
        var urlLock = new object();
        // Буфер всего вывода: TUI (pinggy) шлёт перерисовки кусками и URL может
        // быть разорван между двумя событиями — ищем по всему накопленному.
        var buf = new System.Text.StringBuilder();
        void OnLine(string? line)
        {
            if (string.IsNullOrEmpty(line)) return;
            lock (urlLock)
            {
                if (buf.Length < 32768) buf.AppendLine(line);
                else { buf.Remove(0, 8192); buf.AppendLine(line); }
                if (url == null)
                {
                    try { url ??= t.PickUrl(buf.ToString()); }
                    catch { }
                }
                if (url != null) return;
            }
            // TUI pinggy шлёт километровый ANSI-мусор — в лог только короткие строки.
            if (line.Length > 400) return;
            var s = line;
            try
            {
                s = AnsiRx.Replace(line, "");
            }
            catch { }
            var cut = s.Trim();
            if (cut.Length == 0) return;
            if (cut.Contains("warn", StringComparison.OrdinalIgnoreCase) ||
                cut.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                cut.Contains("denied", StringComparison.OrdinalIgnoreCase) ||
                cut.Contains("timed out", StringComparison.OrdinalIgnoreCase) ||
                cut.Contains("forward", StringComparison.OrdinalIgnoreCase) ||
                cut.Contains("expire", StringComparison.OrdinalIgnoreCase) ||
                cut.Contains("authenticated", StringComparison.OrdinalIgnoreCase))
                AppendLog($"[ssh:{t.Name}] " + Trim(cut, 200));
        }
        try { proc.Start(); }
        catch (Exception ex)
        {
            AppendLog($"[ssh:{t.Name}] start failed: " + Trim(ex.Message, 120));
            try { proc.Dispose(); } catch { }
            return null;
        }
        try { proc.OutputDataReceived += (_, e) => OnLine(e.Data); proc.BeginOutputReadLine(); } catch { }
        try { proc.ErrorDataReceived += (_, e) => OnLine(e.Data); proc.BeginErrorReadLine(); } catch { }
        var deadline = DateTime.UtcNow + wait;
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested && !_userStop)
        {
            lock (urlLock) { if (url != null) break; }
            if (proc.HasExited) break;
            try { await Task.Delay(400, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
        lock (urlLock)
        {
            if (url != null && !proc.HasExited)
            {
                try { proc.CancelOutputRead(); } catch { }
                try { proc.CancelErrorRead(); } catch { }
                AppendLog($"[ssh:{t.Name}] tunnel up: {url}");
                return (proc, url);
            }
        }
        // ВАЖНО: для pinggy обработчики НЕ снимаем жёстко — процесс убиваем целиком.
        try { proc.CancelOutputRead(); } catch { }
        try { proc.CancelErrorRead(); } catch { }
        try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
        try { proc.Dispose(); } catch { }
        AppendLog($"[ssh:{t.Name}] no URL in {wait.TotalSeconds:F0}s — пробую дальше");
        return null;
    }

    private static readonly Regex AnsiRx =
        new(@"\x1B\[[0-9;?]*[A-Za-z]|\x1B\][^\x07]*\x07|\x1B[()][AB012]", RegexOptions.Compiled);

    private static readonly HttpClient _probe = new() { Timeout = TimeSpan.FromSeconds(12) };

    // GET {publicUrl}/api без токена: должен вернуть {"ok":true,...}.
    // null = снаружи отвечает, иначе текст причины для лога.
    private static async Task<string?> ProbePublicAsync(string publicUrl, CancellationToken ct)
    {
        for (int i = 0; i < 2; i++)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(12));
                using var res = await _probe.GetAsync(publicUrl.TrimEnd('/') + "/api", cts.Token).ConfigureAwait(false);
                var body = await res.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
                if (res.IsSuccessStatusCode && body.Contains("\"ok\""))
                    return null;
                // HTML вместо JSON = страница проверки провайдера (challenge):
                // в обычном браузере проходится, во WebView на телефоне — нет.
                if (body.Contains("<html", StringComparison.OrdinalIgnoreCase))
                    return "Провайдер показывает страницу проверки — откройте ссылку в Chrome на телефоне и пройдите её";
                return $"HTTP {(int)res.StatusCode}";
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(2000, ct).ConfigureAwait(false); } catch { }
            }
            catch (Exception ex)
            {
                try { await Task.Delay(2000, ct).ConfigureAwait(false); } catch { return Trim(ex.Message, 120); }
            }
        }
        return "no answer in ~25s (холодный старт edge или блок сети)";
    }

    public void Stop()
    {
        _userStop = true;
        try { _cts?.Cancel(); } catch { }
        try
        {
            if (_proc is { HasExited: false })
            {
                try { _proc.Kill(entireProcessTree: true); } catch { }
            }
        }
        catch { }
        try { _proc?.Dispose(); } catch { }
        _proc = null;
        PublicUrl = null;
        Provider = null;
        try { SleepBlocker.Allow(); } catch { }
        Status = "Stopped";
        Changed?.Invoke();
    }

    private static string Trim(string? s, int n) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s[..n] + "…");

    // Локальный IPv4 для live в одной сети (браузер на том же Wi-Fi / этот же ПК).
    // Внутри Telegram на телефоне из другой сети нужен именно tunnel (HTTPS).
    public static string GetLanIPv4()
    {
        try
        {
            foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType is System.Net.NetworkInformation.NetworkInterfaceType.Loopback or
                    System.Net.NetworkInformation.NetworkInterfaceType.Tunnel) continue;
                foreach (var ip in nic.GetIPProperties().UnicastAddresses)
                {
                    if (ip.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) continue;
                    var b = ip.Address.GetAddressBytes();
                    if (b[0] == 169 && b[1] == 254) continue; // link-local
                    return ip.Address.ToString();
                }
            }
        }
        catch { }
        return "127.0.0.1";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        try { _cts?.Dispose(); } catch { }
    }
}
