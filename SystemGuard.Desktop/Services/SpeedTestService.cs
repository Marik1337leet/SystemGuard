using System;
using System.Diagnostics;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace SystemGuard.Desktop.Services;

// Честный замер скорости скачивания: несколько зеркал цепочкой,
// потоковое чтение с живым прогрессом и средней скоростью.
// Старый тест качал один мёртвый URL cachefly и почти всегда
// заканчивался «Speed test failed».
public static class SpeedTestService
{
    private static readonly (string Name, string Url)[] Mirrors =
    {
        ("Cloudflare", "https://speed.cloudflare.com/__down?bytes=25000000"),
        ("OVH", "https://proof.ovh.net/files/10Mb.dat"),
        ("CacheFly", "https://cachefly.cachefly.net/10mb.test"),
    };

    private static readonly HttpClient _http = new(new HttpClientHandler
    {
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = 5,
    })
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    public static async Task<(bool Ok, double Mbps, string Detail)> RunDownloadTestAsync(
        IProgress<(double Percent, double Mbps)>? progress = null,
        CancellationToken ct = default)
    {
        string lastError = "no mirrors tried";
        foreach (var (name, url) in Mirrors)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(60));
            try
            {
                var res = await TryMirrorAsync(name, url, progress, cts.Token).ConfigureAwait(false);
                if (res.Ok) return res;
                lastError = $"{name}: {res.Detail}";
            }
            catch (OperationCanceledException) { lastError = $"{name}: timed out"; }
            catch (Exception ex) { lastError = $"{name}: {ex.Message}"; }
        }
        return (false, 0, lastError);
    }

    private static async Task<(bool Ok, double Mbps, string Detail)> TryMirrorAsync(
        string name, string url,
        IProgress<(double Percent, double Mbps)>? progress,
        CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.UserAgent.ParseAdd("SystemGuard/1.0");
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        long? total = resp.Content.Headers.ContentLength;
        var sw = Stopwatch.StartNew();
        long received = 0;
        var buffer = new byte[64 * 1024];
        using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        int read;
        var lastReport = TimeSpan.Zero;
        while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            received += read;
            var elapsed = sw.Elapsed;
            if (elapsed - lastReport > TimeSpan.FromMilliseconds(250))
            {
                lastReport = elapsed;
                double mbps = elapsed.TotalSeconds > 0 ? received * 8.0 / elapsed.TotalSeconds / 1_000_000.0 : 0;
                double pct = total is > 0 ? Math.Min(99, received * 100.0 / total.Value) : Math.Min(99, received / 250_000.0);
                try { progress?.Report((pct, mbps)); } catch { }
            }
        }
        sw.Stop();
        if (received <= 0 || sw.Elapsed.TotalSeconds <= 0)
            return (false, 0, "empty response");
        double avg = Math.Round(received * 8.0 / sw.Elapsed.TotalSeconds / 1_000_000.0, 1);
        string detail = $"{name}, {received / 1048576.0:F1} MB in {sw.Elapsed.TotalSeconds:F1}s";
        return (true, avg, detail);
    }
}
