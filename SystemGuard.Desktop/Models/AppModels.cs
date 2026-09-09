using System;

namespace SystemGuard.Desktop.Models;

public class BenchmarkResult
{
    public string TestName { get; set; } = "";
    public double Score { get; set; }
    public string Unit { get; set; } = "";
    public string Details { get; set; } = "";
    public double PercentageVsReference { get; set; }
    public string Rating { get; set; } = "";
    public string Recommendation { get; set; } = "";
}

public class PowerPlanInfo
{
    public string Name { get; set; } = "";
    public string Guid { get; set; } = "";
    public bool IsActive { get; set; }
}

public class BatteryInfo
{
    public int ChargePercent { get; set; }
    public double WearLevel { get; set; }
    public bool IsCharging { get; set; }
    public string Status { get; set; } = "Unknown";
    public TimeSpan? TimeRemaining { get; set; }

    // Используется в PerformanceViewModel.LoadData() как battery.TimeRemainingText
    public string TimeRemainingText =>
        TimeRemaining.HasValue
            ? TimeRemaining.Value.TotalHours >= 1
                ? $"{(int)TimeRemaining.Value.TotalHours}h {TimeRemaining.Value.Minutes}m remaining"
                : $"{TimeRemaining.Value.Minutes}m remaining"
            : "N/A";
}

public class AppSettings
{
    public bool DarkTheme { get; set; } = true;
    public bool StartMinimized { get; set; }
    public bool StartWithWindows { get; set; }
    public bool AutoUpdate { get; set; } = true;
    public bool ShowTrayIcon { get; set; } = true;
    public bool MinimizeToTray { get; set; } = true;
    public string Language { get; set; } = "en";
    public int MonitoringIntervalMs { get; set; } = 1000;
    public bool ShowNotifications { get; set; } = true;
    public string TelegramToken { get; set; } = "";
    public string TelegramChatId { get; set; } = "";
    public string Version { get; set; } = "1.0.0";
    public string LastCheckUpdate { get; set; } = "Never";
}