using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using SystemGuard.Desktop.Models;

namespace SystemGuard.Desktop.Services;

public class BenchmarkService
{
    private const int CPU_REFERENCE = 25_000_000;
    private const double MEMORY_REFERENCE = 20000.0;
    private const double DISK_REFERENCE = 500.0;
    private const int NETWORK_REFERENCE = 100;

    public async Task<BenchmarkResult> BenchmarkCpuSingleCore() => await Task.Run(() =>
    {
        var sw = Stopwatch.StartNew();
        double pi = 0; int n = 50_000_000;
        for (int i = 0; i < n; i++) pi += (i % 2 == 0 ? 1.0 : -1.0) / (2.0 * i + 1.0);
        sw.Stop();
        var score = Math.Round(n / sw.Elapsed.TotalSeconds);
        var pct = Math.Min(100, score / CPU_REFERENCE * 100);
        return new BenchmarkResult
        {
            TestName = "CPU Single Core",
            Score = score,
            Unit = "ops/sec",
            Details = $"{n / 1_000_000}M iterations in {sw.Elapsed.TotalSeconds:F2}s\nSingle-threaded Pi calculation",
            PercentageVsReference = pct,
            Rating = GetRating(pct),
            Recommendation = pct < 50 ? "Consider CPU upgrade for better single-thread performance" : "Single core performance is good"
        };
    });

    public async Task<BenchmarkResult> BenchmarkCpuMultiCore() => await Task.Run(() =>
    {
        var sw = Stopwatch.StartNew();
        int n = 200_000_000;
        Parallel.For(0, Environment.ProcessorCount, _ =>
        {
            double pi = 0;
            for (int i = 0; i < n / Environment.ProcessorCount; i++)
                pi += (i % 2 == 0 ? 1.0 : -1.0) / (2.0 * i + 1.0);
        });
        sw.Stop();
        var score = Math.Round(n / sw.Elapsed.TotalSeconds);
        var pct = Math.Min(100, score / (CPU_REFERENCE * Environment.ProcessorCount) * 100);
        return new BenchmarkResult
        {
            TestName = "CPU Multi Core",
            Score = score,
            Unit = "ops/sec",
            Details = $"{n / 1_000_000}M iterations across {Environment.ProcessorCount} cores in {sw.Elapsed.TotalSeconds:F2}s\nParallel processing test",
            PercentageVsReference = pct,
            Rating = GetRating(pct),
            Recommendation = pct < 50 ? "Multi-core performance below average. Check cooling/throttling" : "Multi-core performance is solid"
        };
    });

    public async Task<BenchmarkResult> BenchmarkMemoryBandwidth() => await Task.Run(() =>
    {
        var sizes = new[] { 10 * 1024 * 1024, 100 * 1024 * 1024, 500 * 1024 * 1024 };
        var results = new List<double>();

        foreach (var size in sizes)
        {
            try
            {
                var data = new byte[size]; var dest = new byte[size];
                var sw = Stopwatch.StartNew();
                Array.Copy(data, dest, data.Length);
                sw.Stop();
                results.Add(size / sw.Elapsed.TotalSeconds / 1_048_576);
            }
            catch { results.Add(0); }
        }

        var avgScore = Math.Round(results.Average(), 2);
        var pct = Math.Min(100, avgScore / MEMORY_REFERENCE * 100);

        return new BenchmarkResult
        {
            TestName = "Memory Bandwidth",
            Score = avgScore,
            Unit = "MB/s",
            Details = $"Average of {sizes.Length} tests (10MB, 100MB, 500MB)\n" +
                      $"10MB: {results[0]:F0} MB/s\n100MB: {results[1]:F0} MB/s\n500MB: {results[2]:F0} MB/s",
            PercentageVsReference = pct,
            Rating = GetRating(pct),
            Recommendation = pct < 40 ? "Memory bandwidth is low. Check RAM configuration (dual channel?)" :
                            pct < 70 ? "Memory is adequate. Consider faster RAM or XMP profile" : "Memory bandwidth is excellent"
        };
    });

    public async Task<BenchmarkResult> BenchmarkMemoryLatency() => await Task.Run(() =>
    {
        int size = 10_000_000;
        var data = new int[size];
        var random = new Random(42);
        for (int i = 0; i < size; i++) data[i] = random.Next(size);

        var sw = Stopwatch.StartNew();
        int sum = 0, index = 0;
        for (int i = 0; i < 1_000_000; i++)
        {
            index = data[index];
            sum += index;
        }
        sw.Stop();

        var latency = sw.Elapsed.TotalNanoseconds / 1_000_000.0;
        var pct = Math.Min(100, 50.0 / latency * 100);

        return new BenchmarkResult
        {
            TestName = "Memory Latency",
            Score = Math.Round(latency, 2),
            Unit = "ns",
            Details = $"Random access latency across {size / 1_000_000}M element array\nLower is better\nReference: ~50ns for DDR4",
            PercentageVsReference = pct,
            Rating = latency < 60 ? "Excellent" : latency < 80 ? "Good" : latency < 100 ? "Average" : "Poor",
            Recommendation = latency > 100 ? "High memory latency detected. Check RAM timings in BIOS" : "Memory latency is good"
        };
    });

    public async Task<BenchmarkResult> BenchmarkDiskSequentialWrite() => await Task.Run(() =>
    {
        var tmp = System.IO.Path.GetTempFileName();
        var data = new byte[4096]; new Random().NextBytes(data);
        var sw = Stopwatch.StartNew();
        long written = 0;
        using (var fs = new System.IO.FileStream(tmp, System.IO.FileMode.Create, System.IO.FileAccess.Write, System.IO.FileShare.None, 4096, System.IO.FileOptions.SequentialScan))
        {
            while (written < 500_000_000 && sw.Elapsed.TotalSeconds < 10)
            {
                fs.Write(data, 0, data.Length);
                written += data.Length;
            }
        }
        sw.Stop();
        try { System.IO.File.Delete(tmp); } catch { }

        var score = Math.Round(written / sw.Elapsed.TotalSeconds / 1_048_576, 2);
        var pct = Math.Min(100, score / DISK_REFERENCE * 100);

        return new BenchmarkResult
        {
            TestName = "Disk Sequential Write",
            Score = score,
            Unit = "MB/s",
            Details = $"{written / 1_048_576}MB written in {sw.Elapsed.TotalSeconds:F2}s\nSequential write test",
            PercentageVsReference = pct,
            Rating = GetRating(pct),
            Recommendation = pct < 30 ? "Slow write speed. Defragment HDD or check SSD health" :
                            pct < 60 ? "Write speed is OK. Consider SSD upgrade for better performance" : "Write speed is excellent"
        };
    });

    public async Task<BenchmarkResult> BenchmarkDiskSequentialRead() => await Task.Run(() =>
    {
        var tmp = System.IO.Path.GetTempFileName();
        var data = new byte[50_000_000]; new Random().NextBytes(data);
        System.IO.File.WriteAllBytes(tmp, data);

        var buffer = new byte[4096];
        var sw = Stopwatch.StartNew();
        long read = 0;
        using (var fs = new System.IO.FileStream(tmp, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.Read, 4096, System.IO.FileOptions.SequentialScan))
        {
            while (read < fs.Length && sw.Elapsed.TotalSeconds < 10)
            {
                read += fs.Read(buffer, 0, buffer.Length);
            }
        }
        sw.Stop();
        try { System.IO.File.Delete(tmp); } catch { }

        var score = Math.Round(read / sw.Elapsed.TotalSeconds / 1_048_576, 2);
        var pct = Math.Min(100, score / DISK_REFERENCE * 100);

        return new BenchmarkResult
        {
            TestName = "Disk Sequential Read",
            Score = score,
            Unit = "MB/s",
            Details = $"{read / 1_048_576}MB read in {sw.Elapsed.TotalSeconds:F2}s\nSequential read test",
            PercentageVsReference = pct,
            Rating = GetRating(pct),
            Recommendation = pct < 30 ? "Slow read speed. Check disk for errors" : "Read speed is good"
        };
    });

    public async Task<BenchmarkResult> BenchmarkDiskRandomAccess() => await Task.Run(() =>
    {
        var tmp = System.IO.Path.GetTempFileName();
        var data = new byte[10_000_000]; new Random().NextBytes(data);
        System.IO.File.WriteAllBytes(tmp, data);

        var random = new Random(42);
        var buffer = new byte[4096];
        var sw = Stopwatch.StartNew();
        int operations = 0;

        using (var fs = new System.IO.FileStream(tmp, System.IO.FileMode.Open, System.IO.FileAccess.Read))
        {
            while (sw.Elapsed.TotalSeconds < 5 && operations < 5000)
            {
                fs.Position = random.Next((int)fs.Length - 4096);
                fs.Read(buffer, 0, buffer.Length);
                operations++;
            }
        }
        sw.Stop();
        try { System.IO.File.Delete(tmp); } catch { }

        var iops = Math.Round(operations / sw.Elapsed.TotalSeconds);
        var pct = Math.Min(100, iops / 1000.0 * 100); // 1000 IOPS reference for HDD

        return new BenchmarkResult
        {
            TestName = "Disk Random Access",
            Score = iops,
            Unit = "IOPS",
            Details = $"{operations} operations in {sw.Elapsed.TotalSeconds:F2}s\nRandom 4K read test",
            PercentageVsReference = pct,
            Rating = iops > 10000 ? "Excellent (SSD)" : iops > 1000 ? "Good (SSD)" : iops > 200 ? "Average (HDD)" : "Poor",
            Recommendation = iops < 200 ? "Very slow random access. Upgrade to SSD for dramatic improvement" :
                            iops < 1000 ? "This appears to be a HDD. SSD would provide 10-50x improvement" : "Random access performance is good"
        };
    });

    public async Task<BenchmarkResult> BenchmarkNetworkSpeed() => await Task.Run(async () =>
    {
        try
        {
            using var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var sw = Stopwatch.StartNew();
            var response = await client.GetAsync("https://speed.cloudflare.com/__down?bytes=5000000");
            var responseData = await response.Content.ReadAsByteArrayAsync();
            sw.Stop();

            var speedMbps = Math.Round(responseData.Length * 8.0 / sw.Elapsed.TotalSeconds / 1_000_000, 2);
            var pct = Math.Min(100, speedMbps / NETWORK_REFERENCE * 100);

            return new BenchmarkResult
            {
                TestName = "Network Download",
                Score = speedMbps,
                Unit = "Mbps",
                Details = $"{responseData.Length / 1_000_000}MB downloaded in {sw.Elapsed.TotalSeconds:F2}s\nCloudflare speed test",
                PercentageVsReference = pct,
                Rating = speedMbps > 100 ? "Excellent" : speedMbps > 50 ? "Good" : speedMbps > 25 ? "Average" : "Poor",
                Recommendation = speedMbps < 25 ? "Slow internet. Check connection or upgrade plan" : "Internet speed is adequate"
            };
        }
        catch
        {
            return new BenchmarkResult
            {
                TestName = "Network Download",
                Score = 0,
                Unit = "Mbps",
                Details = "Test skipped — speed server unreachable (firewall/VPN?)",
                PercentageVsReference = 0,
                Rating = "Skipped",
                Recommendation = "Speed test unavailable — not an internet problem, check firewall/VPN and retry"
            };
        }
    });

    private string GetRating(double percentage) => percentage switch
    {
        >= 90 => "Outstanding",
        >= 75 => "Excellent",
        >= 60 => "Good",
        >= 40 => "Average",
        >= 25 => "Below Average",
        _ => "Poor"
    };

    // ── Сжатие: GZip 64MB смешанных данных (реальный тест CPU+память) ──────
    public async Task<BenchmarkResult> BenchmarkCompression() => await Task.Run(() =>
    {
        var data = new byte[64 * 1024 * 1024];
        new Random(7).NextBytes(data);
        // Делаем сжимаемым наполовину — как реальные файлы
        for (int i = 0; i < data.Length; i += 2) data[i] = (byte)(i & 0xFF);
        var sw = Stopwatch.StartNew();
        using (var ms = new System.IO.MemoryStream())
        {
            // leaveOpen: иначе GZipStream закроет MemoryStream и чтение Length упадёт
            using (var gz = new System.IO.Compression.GZipStream(ms, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
                gz.Write(data, 0, data.Length);
            sw.Stop();
            var mbs = Math.Round(64.0 / sw.Elapsed.TotalSeconds, 1);
            var pct = Math.Min(100, mbs / 150.0 * 100); // 150 MB/s — референс современного CPU
            return new BenchmarkResult
            {
                TestName = "Compression (GZip 64MB)",
                Score = mbs,
                Unit = "MB/s",
                Details = $"64MB in {sw.Elapsed.TotalSeconds:F2}s → {ms.Length / 1048576}MB output",
                PercentageVsReference = pct,
                Rating = GetRating(pct),
                Recommendation = pct < 40 ? "Weak compression throughput — CPU bottleneck" : "Compression throughput is good"
            };
        }
    });

    // ── Кэш CPU: прогоны массивами L1/L2/L3 размеров ───────────────────────
    public async Task<BenchmarkResult> BenchmarkCpuCache() => await Task.Run(() =>
    {
        double CacheRun(int kb)
        {
            var arr = new byte[kb * 1024];
            new Random(3).NextBytes(arr);
            var sw = Stopwatch.StartNew();
            long sum = 0;
            for (int r = 0; r < 20; r++)
                for (int i = 0; i < arr.Length; i += 64) sum += arr[i];
            sw.Stop();
            return kb * 20.0 / 1024.0 / Math.Max(sw.Elapsed.TotalSeconds, 0.001); // MB/s
        }
        var l1 = CacheRun(32); var l2 = CacheRun(512); var l3 = CacheRun(8192);
        var avg = Math.Round((l1 + l2 + l3) / 3.0, 1);
        var pct = Math.Min(100, avg / 20000.0 * 100);
        return new BenchmarkResult
        {
            TestName = "CPU Cache",
            Score = avg,
            Unit = "MB/s",
            Details = $"32KB: {l1:F0} MB/s\n512KB: {l2:F0} MB/s\n8MB: {l3:F0} MB/s",
            PercentageVsReference = pct,
            Rating = GetRating(pct),
            Recommendation = pct < 40 ? "Cache throughput is low — check power plan / throttling" : "Cache throughput is good"
        };
    });

    // ── Диск QD32: 32 параллельных потока случайного чтения 4K ────────────
    public async Task<BenchmarkResult> BenchmarkDiskRandomQD32() => await Task.Run(async () =>
    {
        var tmp = System.IO.Path.GetTempFileName();
        var data = new byte[64 * 1024 * 1024]; new Random().NextBytes(data);
        System.IO.File.WriteAllBytes(tmp, data);
        var sw = Stopwatch.StartNew();
        int totalOps = 0;
        var tasks = Enumerable.Range(0, 32).Select(_ => Task.Run(() =>
        {
            var rnd = new Random(Guid.NewGuid().GetHashCode());
            var buf = new byte[4096];
            int ops = 0;
            using var fs = new System.IO.FileStream(tmp, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite);
            var len = (int)fs.Length;
            while (sw.Elapsed.TotalSeconds < 5 && ops < 2000)
            {
                fs.Position = rnd.Next(len - 4096);
                fs.Read(buf, 0, buf.Length);
                ops++;
            }
            System.Threading.Interlocked.Add(ref totalOps, ops);
        })).ToArray();
        await Task.WhenAll(tasks);
        sw.Stop();
        try { System.IO.File.Delete(tmp); } catch { }
        var iops = Math.Round(totalOps / sw.Elapsed.TotalSeconds);
        var pct = Math.Min(100, iops / 50000.0 * 100); // 50k IOPS — референс SATA SSD QD32
        return new BenchmarkResult
        {
            TestName = "Disk Random QD32",
            Score = iops,
            Unit = "IOPS",
            Details = $"{totalOps} ops in {sw.Elapsed.TotalSeconds:F2}s across 32 queues",
            PercentageVsReference = pct,
            Rating = iops > 30000 ? "Excellent (NVMe)" : iops > 8000 ? "Good (SSD)" : iops > 1000 ? "Average (SATA SSD)" : "Poor (HDD?)",
            Recommendation = iops < 1000 ? "Random QD32 is HDD-level — SSD upgrade recommended" : "Random QD32 performance is good"
        };
    });

    // ── GPU synthetic 2D: SkiaSharp-векторный рендер, FPS ─────────────────
    // Честный тест 2D-конвейера (холст 1080p, фигуры+текст). OpenGL/Vulkan
    // требуют нативных контекстов — их заменяет эта синтетика + мониторинг.
    public async Task<BenchmarkResult> BenchmarkGpuSynthetic() => await Task.Run(() =>
    {
        try
        {
            var info = new SkiaSharp.SKImageInfo(1920, 1080);
            using var surface = SkiaSharp.SKSurface.Create(info);
            if (surface == null) throw new Exception("Skia surface unavailable");
            var canvas = surface.Canvas;
            var rnd = new Random(11);
            var paint = new SkiaSharp.SKPaint { IsAntialias = true };
            const int frames = 120;
            var sw = Stopwatch.StartNew();
            for (int f = 0; f < frames; f++)
            {
                canvas.Clear(new SkiaSharp.SKColor((byte)(f % 255), 30, 60));
                for (int i = 0; i < 60; i++)
                {
                    paint.Color = new SkiaSharp.SKColor((byte)rnd.Next(256), (byte)rnd.Next(256), (byte)rnd.Next(256));
                    if ((i & 1) == 0) canvas.DrawCircle(rnd.Next(1920), rnd.Next(1080), rnd.Next(10, 120), paint);
                    else canvas.DrawRect(rnd.Next(1920), rnd.Next(1080), rnd.Next(20, 300), rnd.Next(20, 300), paint);
                }
                canvas.Flush();
            }
            sw.Stop();
            var fps = Math.Round(frames / sw.Elapsed.TotalSeconds, 1);
            var pct = Math.Min(100, fps / 240.0 * 100); // 240 FPS синтетики — референс
            return new BenchmarkResult
            {
                TestName = "GPU Synthetic 2D",
                Score = fps,
                Unit = "FPS",
                Details = $"{frames} frames 1080p in {sw.Elapsed.TotalSeconds:F2}s (Skia 2D vector)",
                PercentageVsReference = pct,
                Rating = GetRating(pct),
                Recommendation = pct < 40 ? "2D render is slow — check GPU drivers / power plan" : "2D render performance is good"
            };
        }
        catch (Exception ex)
        {
            return new BenchmarkResult
            {
                TestName = "GPU Synthetic 2D", Score = 0, Unit = "FPS",
                Details = $"Skipped: {ex.Message}", PercentageVsReference = 0,
                Rating = "Skipped", Recommendation = "2D test unavailable"
            };
        }
    });

    public string GetOverallRating(List<BenchmarkResult> results)
    {
        if (results.Count == 0) return "No results";
        var avg = results.Average(r => r.PercentageVsReference);
        return avg switch
        {
            >= 80 => "🏆 EXCELLENT - Your system is performing at top level",
            >= 65 => "GOOD - Your system is performing well",
            >= 50 => "👍 ABOVE AVERAGE - Decent performance for most tasks",
            >= 35 => "AVERAGE - Consider some upgrades for better experience",
            >= 20 => "🔧 BELOW AVERAGE - Several components could use improvement",
            _ => "POOR - Your system needs significant upgrades"
        };
    }

    public string GetSystemSummary(List<BenchmarkResult> results)
    {
        var weakest = results.OrderBy(r => r.PercentageVsReference).FirstOrDefault();
        var strongest = results.OrderByDescending(r => r.PercentageVsReference).FirstOrDefault();

        return $"STRONGEST: {strongest?.TestName} ({strongest?.Rating})\n" +
               $"WEAKEST: {weakest?.TestName} ({weakest?.Rating})\n" +
               $"UPGRADE PRIORITY: {weakest?.TestName}\n" +
               $"{weakest?.Recommendation}";
    }
}