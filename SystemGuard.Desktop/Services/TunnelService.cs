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

    public async Task<(bool Ok, string Message)> EnsureBinaryAsync(
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (BinaryExists) return (true, "cloudflared ready");
        try
        {
            const string url = "https://github.com/cloudflare/cloudflared/releases/latest/download/cloudflared-windows-amd64.exe";
            var dir = Path.GetDirectoryName(BinaryPath)!;
            Directory.CreateDirectory(dir);
            var tmp = BinaryPath + ".download";
            progress?.Report("Downloading cloudflared (~30 MB)…");
            using var res = await _dl.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            res.EnsureSuccessStatusCode();
            await using var net = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var fs = File.Open(tmp, FileMode.Create, FileAccess.Write, FileShare.None);
            await net.CopyToAsync(fs, ct).ConfigureAwait(false);
            if (File.Exists(BinaryPath)) File.Delete(BinaryPath);
            File.Move(tmp, BinaryPath);
            progress?.Report("cloudflared ready");
            return (true, "cloudflared ready");
        }
        catch (Exception ex)
        {
            return (false, $"Tunnel binary download failed: {Trim(ex.Message, 160)}");
        }
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

    private async Task<string?> WaitForUrlAsync(Process p, TimeSpan timeout, CancellationToken ct)
    {
        var rx = new Regex(@"https://[a-zA-Z0-9-]+\.trycloudflare\.com", RegexOptions.Compiled);
        var deadline = DateTime.UtcNow + timeout;
        var err = p.StandardError;
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            if (p.HasExited)
            {
                try
                {
                    var rest = await err.ReadToEndAsync().ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(rest)) AppendLog(rest.Trim());
                    var m2 = rx.Match(rest ?? "");
                    if (m2.Success) return m2.Value;
                }
                catch { }
                AppendLog($"cloudflared exited (code {p.ExitCode})");
                return null;
            }
            var task = err.ReadLineAsync();
            var done = await Task.WhenAny(task, Task.Delay(500, ct)).ConfigureAwait(false);
            if (done != task) continue;
            var line = await task.ConfigureAwait(false);
            if (line == null) { await Task.Delay(300, ct).ConfigureAwait(false); continue; }
            if (line.Contains("trycloudflare", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("ERR", StringComparison.Ordinal))
                AppendLog(Trim(line, 220));
            var m = rx.Match(line);
            if (m.Success) return m.Value;
        }
        return null;
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
