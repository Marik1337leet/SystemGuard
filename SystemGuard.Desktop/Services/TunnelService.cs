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
    public string Status { get; private set; } = "Stopped";
    public string LastLog { get; private set; } = "";
    public event Action? Changed;

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

    public async Task<(bool Ok, string Message)> StartAsync(int localPort, CancellationToken ct = default)
    {
        if (IsRunning) return (true, PublicUrl ?? "already running");
        if (!BinaryExists) return (false, "cloudflared not downloaded");
        Stop();
        _cts = new CancellationTokenSource();
        PublicUrl = null;
        LastLog = "";
        Status = "Starting…";
        Changed?.Invoke();
        try
        {
            _proc = new Process
            {
                StartInfo = new ProcessStartInfo(BinaryPath, $"tunnel --url http://127.0.0.1:{localPort}")
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true
                }
            };
            _proc.Start();
            // URL прилетает в stderr вида https://xxx.trycloudflare.com
            var found = await WaitForUrlAsync(_proc, TimeSpan.FromSeconds(60), _cts.Token).ConfigureAwait(false);
            if (found == null)
            {
                var log = LastLog.Trim();
                Stop();
                Status = string.IsNullOrEmpty(log)
                    ? "Tunnel failed: no output in 60s (blocked network/antivirus?)"
                    : "Tunnel failed. Log tail:\n" + log;
                Changed?.Invoke();
                return (false, Status);
            }
            PublicUrl = found;
            Status = "Live: " + found;
            Changed?.Invoke();
            // Добиваем процесс при выходе
            _ = Task.Run(async () =>
            {
                try { await _proc.WaitForExitAsync(_cts.Token).ConfigureAwait(false); }
                catch { }
                PublicUrl = null;
                Status = "Stopped";
                Changed?.Invoke();
            });
            return (true, PublicUrl);
        }
        catch (Exception ex)
        {
            Stop();
            Status = $"Tunnel error: {Trim(ex.Message, 140)}";
            Changed?.Invoke();
            return (false, Status);
        }
    }

    private readonly object _urlLock = new();
    private string? _foundUrl;

    private void CheckLine(string? line)
    {
        if (string.IsNullOrEmpty(line)) return;
        var m = UrlRx.Match(line);
        if (m.Success)
        {
            lock (_urlLock) _foundUrl ??= m.Value;
            return;
        }
        if (line.Contains("trycloudflare", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("error", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("ERR", StringComparison.Ordinal))
            AppendLog(Trim(line, 220));
    }

    private static readonly Regex UrlRx =
        new(@"https://[a-zA-Z0-9-]+\.trycloudflare\.com", RegexOptions.Compiled);

    private async Task<string?> WaitForUrlAsync(Process p, TimeSpan timeout, CancellationToken ct)
    {
        _foundUrl = null;
        // cloudflared пишет URL в stderr, но новые версии могут и в stdout —
        // слушаем оба. Stdout — через события, stderr — строго ОДИН pending
        // ReadLine (повторный ReadLine на том же StreamReader роняет всё
        // с "stream is currently in use" — это и убивало кнопку Publish).
        try { p.OutputDataReceived += (_, e) => CheckLine(e.Data); p.BeginOutputReadLine(); }
        catch { }
        var deadline = DateTime.UtcNow + timeout;
        var err = p.StandardError;
        Task<string?>? pending = null;
        try
        {
            while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                lock (_urlLock) { if (_foundUrl != null) return _foundUrl; }
                if (p.HasExited)
                {
                    if (pending != null)
                    {
                        try { CheckLine(await pending.ConfigureAwait(false)); } catch { }
                        pending = null;
                        lock (_urlLock) { if (_foundUrl != null) return _foundUrl; }
                    }
                    try
                    {
                        var rest = await err.ReadToEndAsync().ConfigureAwait(false);
                        if (!string.IsNullOrWhiteSpace(rest))
                        {
                            AppendLog(Trim(rest.Trim(), 400));
                            CheckLine(rest);
                        }
                    }
                    catch { }
                    lock (_urlLock) { if (_foundUrl != null) return _foundUrl; }
                    AppendLog($"cloudflared exited (code {p.ExitCode})");
                    return null;
                }
                pending ??= err.ReadLineAsync();
                try
                {
                    var done = await Task.WhenAny(pending, Task.Delay(500, ct)).ConfigureAwait(false);
                    if (done != pending) continue; // таймаут тика — ждём ТОТ ЖЕ read
                    CheckLine(await pending.ConfigureAwait(false));
                    pending = null;
                }
                catch (OperationCanceledException) { return null; }
                catch (Exception ex)
                {
                    AppendLog("log read: " + Trim(ex.Message, 120));
                    pending = null;
                    try { await Task.Delay(500, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return null; }
                }
            }
            lock (_urlLock) return _foundUrl;
        }
        finally
        {
            try { p.CancelOutputRead(); } catch { }
        }
    }

    public void Stop()
    {
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
