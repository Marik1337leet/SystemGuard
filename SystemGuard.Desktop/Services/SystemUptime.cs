using System;
using System.Management;

namespace SystemGuard.Desktop.Services;

// Точное время работы системы. Environment.TickCount64 врёт: останавливается
// во сне/гибернации и сбрасывается, поэтому uptime "плавал".
// Берём реальное время загрузки из WMI (Win32_OperatingSystem.LastBootUpTime)
// один раз за сессию и считаем от него.
public static class SystemUptime
{
    private static DateTime? _bootTime;
    private static readonly object _lock = new();

    public static DateTime BootTime
    {
        get
        {
            lock (_lock)
            {
                if (_bootTime == null || _bootTime > DateTime.Now)
                    _bootTime = QueryBootTime();
                return _bootTime.Value;
            }
        }
    }

    public static TimeSpan Uptime => DateTime.Now - BootTime;

    public static string UptimeText
    {
        get
        {
            var up = Uptime;
            if (up.TotalDays >= 1) return $"{(int)up.TotalDays}d {up.Hours}h {up.Minutes}m";
            if (up.TotalHours >= 1) return $"{(int)up.TotalHours}h {up.Minutes}m";
            if (up.TotalMinutes >= 1) return $"{(int)up.Minutes}m {up.Seconds}s";
            return $"{up.Seconds}s";
        }
    }

    private static DateTime QueryBootTime()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT LastBootUpTime FROM Win32_OperatingSystem");
            foreach (ManagementObject o in searcher.Get())
            {
                var raw = o["LastBootUpTime"]?.ToString();
                if (!string.IsNullOrEmpty(raw))
                {
                    var bt = ManagementDateTimeConverter.ToDateTime(raw);
                    if (bt > DateTime.Now.AddMinutes(-1) && bt <= DateTime.Now.AddMinutes(1))
                    {
                        // Только что загрузились — ок
                    }
                    if (bt <= DateTime.Now) return bt;
                }
            }
        }
        catch { }
        try { return DateTime.Now - TimeSpan.FromMilliseconds(Environment.TickCount64); }
        catch { return DateTime.Now; }
    }
}
