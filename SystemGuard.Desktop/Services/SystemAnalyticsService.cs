using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;

namespace SystemGuard.Desktop.Services;

// Продвинутая аналитика без ML-зависимостей:
// прогноз перегрева линейной регрессией, детекция аномалий (z-score),
// статистика аптайма, анализ причин BSOD (дампы + журнал BugCheck).
public class OverheatForecast
{
    public bool HasTrend { get; set; }
    public double DegreesPerMinute { get; set; }
    public double? MinutesTo80C { get; set; }
    public string Verdict { get; set; } = "Not enough data";
}

public class AnomalyHit
{
    public DateTime Time { get; set; }
    public string Metric { get; set; } = "";
    public double Value { get; set; }
    public double ZScore { get; set; }
}

public static class HealthPredictor
{
    // Линейная регрессия по температуре → прогноз минут до 80°C.
    public static OverheatForecast ForecastCpuTemp(IReadOnlyList<MonitoringSample> samples, double warnAt = 80)
    {
        var pts = samples.Where(s => s.CpuTemp > 0).TakeLast(120).ToList();
        if (pts.Count < 6) return new OverheatForecast();
        var t0 = pts[0].Time;
        var xs = pts.Select(p => (p.Time - t0).TotalMinutes).ToArray();
        var ys = pts.Select(p => p.CpuTemp).ToArray();
        var n = xs.Length;
        var mx = xs.Average(); var my = ys.Average();
        var denom = xs.Sum(x => (x - mx) * (x - mx));
        if (denom <= 0) return new OverheatForecast { HasTrend = true, DegreesPerMinute = 0, Verdict = "Temperature stable" };
        var slope = xs.Zip(ys, (x, y) => (x - mx) * (y - my)).Sum() / denom;
        var last = ys[^1];
        var f = new OverheatForecast { HasTrend = true, DegreesPerMinute = Math.Round(slope, 2) };
        if (last >= warnAt) { f.Verdict = $"Already at {last:F0}°C — cool the system now"; return f; }
        if (slope <= 0.05) { f.Verdict = $"Stable (~{last:F0}°C, trend {slope:+0.00;-0.00}°/min)"; return f; }
        f.MinutesTo80C = Math.Round((warnAt - last) / slope, 1);
        f.Verdict = f.MinutesTo80C < 10
            ? $"OVERHEAT RISK: ~{last:F0}°C rising {slope:F2}°/min → {warnAt}°C in ~{f.MinutesTo80C:F0} min"
            : $"Warming: ~{last:F0}°C, {warnAt}°C in ~{f.MinutesTo80C:F0} min at current trend";
        return f;
    }

    public static string FailureRisk(IReadOnlyList<MonitoringSample> samples)
    {
        if (samples.Count == 0) return "No data";
        var recent = samples.TakeLast(60).ToList();
        var hot = recent.Count(s => s.CpuTemp >= 90);
        var diskLow = recent.Any(s => s.DiskFreeGb >= 0 && s.DiskFreeGb < 2);
        if (hot >= 10) return "HIGH: sustained ≥90°C — check cooling/paste/dust";
        if (hot >= 3) return "ELEVATED: temperature spikes ≥90°C observed";
        if (diskLow) return "ELEVATED: system disk critically full (<2GB)";
        return "LOW: no sustained overheating or disk pressure";
    }
}

public static class AnomalyDetector
{
    // Z-score по скользящему окну: |z| ≥ 3 — аномалия.
    public static List<AnomalyHit> Detect(IReadOnlyList<MonitoringSample> samples, double threshold = 3.0)
    {
        var hits = new List<AnomalyHit>();
        var pts = samples.TakeLast(500).ToList();
        if (pts.Count < 20) return hits;
        Check(pts.Select(p => p.CpuLoad).ToList(), pts, "CPU load", threshold, hits);
        Check(pts.Select(p => p.CpuTemp).ToList(), pts, "CPU temp", threshold, hits);
        Check(pts.Select(p => p.RamPercent).ToList(), pts, "RAM", threshold, hits);
        return hits.OrderByDescending(h => Math.Abs(h.ZScore)).Take(20).ToList();
    }

    private static void Check(List<double> vals, List<MonitoringSample> pts, string metric, double threshold, List<AnomalyHit> hits)
    {
        var mean = vals.Average();
        var sd = Math.Sqrt(vals.Sum(v => (v - mean) * (v - mean)) / vals.Count);
        if (sd <= 0.01) return;
        for (int i = 0; i < vals.Count; i++)
        {
            var z = (vals[i] - mean) / sd;
            if (Math.Abs(z) >= threshold)
                hits.Add(new AnomalyHit { Time = pts[i].Time, Metric = metric, Value = Math.Round(vals[i], 1), ZScore = Math.Round(z, 2) });
            if (hits.Count > 60) break;
        }
    }
}

public static class UptimeService
{
    public static TimeSpan SessionUptime() => TimeSpan.FromMilliseconds(Environment.TickCount64);

    public static DateTime LastBoot()
    {
        try
        {
            using var s = new ManagementObjectSearcher("SELECT LastBootUpTime FROM Win32_OperatingSystem");
            foreach (ManagementObject o in s.Get())
            {
                var raw = o["LastBootUpTime"]?.ToString() ?? "";
                if (raw.Length >= 14 && DateTime.TryParseExact(raw[..14], "yyyyMMddHHmmss",
                    null, System.Globalization.DateTimeStyles.None, out var dt))
                    return dt;
            }
        }
        catch { }
        return DateTime.Now - SessionUptime();
    }

    public static List<string> BootHistory(int max = 20)
    {
        var list = new List<string>();
        try
        {
            using var log = new EventLog("System");
            var entries = log.Entries.Cast<System.Diagnostics.EventLogEntry>()
                .Where(e => (e.InstanceId == 6005 || e.InstanceId == 6006 || e.InstanceId == 6008) && e.Source == "EventLog")
                .TakeLast(max * 4).TakeLast(max).ToList();
            foreach (var e in entries)
                list.Add($"{e.TimeGenerated:G} — {(e.InstanceId == 6005 ? "start" : e.InstanceId == 6006 ? "stop" : "unexpected shutdown")}");
        }
        catch { }
        return list;
    }

    public static string Format(TimeSpan up) =>
        up.TotalDays >= 1 ? $"{(int)up.TotalDays}d {up.Hours}h {up.Minutes}m" : $"{up.Hours}h {up.Minutes}m";
}

public class BsodDumpInfo
{
    public string Path { get; set; } = "";
    public DateTime Created { get; set; }
    public long SizeKb { get; set; }
}

public static class BsodService
{
    public static List<BsodDumpInfo> ListMinidumps()
    {
        var list = new List<BsodDumpInfo>();
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Minidump");
            if (!Directory.Exists(dir)) return list;
            foreach (var f in Directory.GetFiles(dir, "*.dmp"))
            {
                try
                {
                    var fi = new FileInfo(f);
                    list.Add(new BsodDumpInfo { Path = f, Created = fi.CreationTime, SizeKb = fi.Length / 1024 });
                }
                catch { }
            }
        }
        catch { }
        return list.OrderByDescending(d => d.Created).ToList();
    }

    public static string FullMemoryDumpStatus()
    {
        try
        {
            var mem = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "MEMORY.DMP");
            return File.Exists(mem)
                ? $"MEMORY.DMP present ({new FileInfo(mem).Length / 1048576}MB, {File.GetCreationTime(mem):G})"
                : "No full MEMORY.DMP";
        }
        catch (Exception ex) { return ex.Message; }
    }

    // Причины синих экранов из журнала: источник BugCheck / Kernel-Power 41.
    public static List<string> BugCheckCauses(int max = 10)
    {
        var list = new List<string>();
        try
        {
            using var log = new EventLog("System");
            var entries = log.Entries.Cast<System.Diagnostics.EventLogEntry>()
                .Where(e => (e.Source.Contains("BugCheck") && (e.InstanceId == 1001 || e.InstanceId == 1003))
                    || (e.Source.Contains("Kernel-Power") && e.InstanceId == 41))
                .TakeLast(max * 6).TakeLast(max).ToList();
            foreach (var e in entries.OrderByDescending(x => x.TimeGenerated))
            {
                var first = (e.Message ?? "").Split('\n').Take(3).Select(l => l.Trim()).Where(l => l.Length > 0);
                list.Add($"{e.TimeGenerated:G} [{e.Source}] " + string.Join(" | ", first).Trim());
            }
        }
        catch (Exception ex) { list.Add($"Event log unavailable: {ex.Message}"); }
        if (list.Count == 0) list.Add("No BugCheck/Kernel-Power 41 events — no recorded BSODs");
        return list;
    }
}
