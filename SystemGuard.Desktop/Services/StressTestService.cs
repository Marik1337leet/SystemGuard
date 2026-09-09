using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SystemGuard.Desktop.Services;

public class StressTestResult
{
    public int DurationSeconds { get; set; }
    public double AvgLoad { get; set; }
    public double MaxTemp { get; set; }
    public bool ThrottlingDetected { get; set; }
    public string Details { get; set; } = "";
}

// Стресс-тест стабильности CPU: грузим все ядра N секунд, семплируем нагрузку,
// детектим троттлинг (падение нагрузки >25% от пика при высокой температуре).
public static class StressTestService
{
    public static async Task<StressTestResult> RunCpuStressAsync(
        int seconds,
        Func<(double Load, double Temp)> sampler,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        seconds = Math.Clamp(seconds, 5, 600);
        var loads = new List<double>();
        var temps = new List<double>();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var workers = Enumerable.Range(0, Environment.ProcessorCount).Select(_ => Task.Run(() =>
        {
            double x = 1.0;
            var sw = Stopwatch.StartNew();
            while (!cts.Token.IsCancellationRequested && sw.Elapsed.TotalSeconds < seconds)
            {
                for (int i = 0; i < 200000; i++) x = Math.Sin(x) * Math.Cos(x) + Math.Sqrt(Math.Abs(x) + 1);
            }
        })).ToArray();

        var t0 = DateTime.Now;
        while ((DateTime.Now - t0).TotalSeconds < seconds)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                var (load, temp) = sampler();
                loads.Add(load);
                temps.Add(temp);
                progress?.Report($"Stress {((DateTime.Now - t0).TotalSeconds):F0}/{seconds}s — CPU {load:F0}% {temp:F0}°C");
            }
            catch { }
            await Task.Delay(1000, ct).ContinueWith(_ => { });
        }

        cts.Cancel();
        try { await Task.WhenAll(workers); } catch { }

        var peak = loads.Count > 0 ? loads.Max() : 0;
        var tail = loads.Count > 3 ? loads.TakeLast(3).Average() : peak;
        bool throttling = peak > 50 && tail < peak * 0.75;

        return new StressTestResult
        {
            DurationSeconds = seconds,
            AvgLoad = loads.Count > 0 ? Math.Round(loads.Average(), 1) : 0,
            MaxTemp = temps.Count > 0 ? Math.Round(temps.Max(), 1) : 0,
            ThrottlingDetected = throttling,
            Details = throttling
                ? $"Possible throttling: peak {peak:F0}% → tail {tail:F0}%. Check cooling."
                : $"Stable: avg load { (loads.Count > 0 ? loads.Average() : 0):F0}%, max temp {(temps.Count > 0 ? temps.Max() : 0):F0}°C."
        };
    }
}
