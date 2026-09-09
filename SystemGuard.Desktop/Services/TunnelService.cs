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
    public event Action? Changed;

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
            var found = await WaitForUrlAsync(_proc, TimeSpan.FromSeconds(45), _cts.Token).ConfigureAwait(false);
            if (found == null)
            {
                Stop();
                Status = "Tunnel failed to start (network blocked?)";
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

    private static async Task<string?> WaitForUrlAsync(Process p, TimeSpan timeout, CancellationToken ct)
    {
        var rx = new Regex(@"https://[a-zA-Z0-9-]+\.trycloudflare\.com", RegexOptions.Compiled);
        var deadline = DateTime.UtcNow + timeout;
        var err = p.StandardError;
        var sb = new System.Text.StringBuilder();
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            if (p.HasExited) return null;
            var task = err.ReadLineAsync();
            var done = await Task.WhenAny(task, Task.Delay(500, ct)).ConfigureAwait(false);
            if (done != task) continue;
            var line = await task.ConfigureAwait(false);
            if (line == null) { await Task.Delay(300, ct).ConfigureAwait(false); continue; }
            sb.AppendLine(line);
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        try { _cts?.Dispose(); } catch { }
    }
}
