// SystemGuard.Core — неизменяемые снимки данных (record types)
// Используются вместо мутабельных ObservableObject моделей в сервисах

namespace SystemGuard.Core.Interfaces;

// ── Hardware ──────────────────────────────────────────────────────────────────

public record HardwareSnapshot(
    string CpuName,
    float CpuLoad,
    float CpuTemperature,
    float CpuClock,
    float CpuPower,
    string GpuName,
    float GpuLoad,
    float GpuTemperature,
    float GpuMemoryUsedMb,
    float GpuMemoryTotalMb,
    float MemoryUsedGb,
    float MemoryAvailableGb,
    float MemoryLoadPercent,
    float NetworkDownloadBps,
    float NetworkUploadBps,
    IReadOnlyList<DriveSnapshot> Drives
);

public record DriveSnapshot(
    string Name,
    float TemperatureCelsius,
    float UsedSpaceGb,
    float TotalSpaceGb,
    float ReadSpeedBps,
    float WriteSpeedBps
);

// ── Process ───────────────────────────────────────────────────────────────────

public record ProcessSnapshot(
    int Pid,
    string Name,
    string FullPath,
    double CpuPercent,
    double MemoryMb,
    int ThreadCount,
    int HandleCount,
    string CommandLine,
    string User,
    DateTime StartTime,
    bool IsResponding,
    bool IsSuspended,
    string Priority,
    string Company,
    string Description,
    int ParentPid,
    int IndentLevel
);

// ── Network ───────────────────────────────────────────────────────────────────

public record AdapterSnapshot(
    string Name,
    string Description,
    string Type,
    string Status,
    bool IsConnected,
    string IpAddress,
    string MacAddress,
    string Gateway,
    string DnsServers,
    string SubnetMask,
    long SpeedBps
);

public record NetworkTrafficSnapshot(
    double DownloadMbps,
    double UploadMbps,
    long TotalDownloadedBytes,
    long TotalUploadedBytes
);

// ── Disk ──────────────────────────────────────────────────────────────────────

public record DiskSnapshot(
    string DriveLetter,
    string Label,
    string FileSystem,
    long TotalBytes,
    long FreeBytes,
    bool IsSsd,
    string HealthStatus
)
{
    public long UsedBytes => TotalBytes - FreeBytes;
    public double UsagePercent => TotalBytes > 0
        ? Math.Round((double)UsedBytes / TotalBytes * 100, 1)
        : 0;
}

public record FolderSizeSnapshot(string Path, string Name, long SizeBytes);
public record LargeFileSnapshot(string Path, string Name, long SizeBytes);

public record DuplicateGroup(
    string Hash,
    long FileSizeBytes,
    IReadOnlyList<string> Paths
)
{
    public long WastedBytes => FileSizeBytes * (Paths.Count - 1);
}

// ── Cleanup ───────────────────────────────────────────────────────────────────

public record CleanupResult(
    long TempFilesBytes,
    long BrowserCacheBytes,
    long WindowsUpdateBytes,
    long PrefetchBytes
)
{
    public long TotalBytes => TempFilesBytes + BrowserCacheBytes + WindowsUpdateBytes + PrefetchBytes;
}

// ── Power ─────────────────────────────────────────────────────────────────────

public record PowerPlanSnapshot(string Name, string Guid, bool IsActive);

public record BatterySnapshot(
    int ChargePercent,
    bool IsCharging,
    double WearLevel,
    TimeSpan? TimeRemaining,
    string Status  // "Charging", "Discharging", "Full", "Unknown"
);

// ── License ───────────────────────────────────────────────────────────────────

public record LicenseSnapshot(
    string Key,
    string MachineId,
    DateTime ActivatedAt,
    DateTime ExpiresAt,
    string Tier,
    bool IsValid
)
{
    public bool IsExpired => DateTime.Now > ExpiresAt;
    public int DaysLeft => IsValid && !IsExpired
        ? Math.Max(0, (int)(ExpiresAt - DateTime.Now).TotalDays)
        : 0;
    public bool IsTrial => Tier == "Free" && IsValid;

    public static LicenseSnapshot Empty => new("", "", DateTime.MinValue, DateTime.MinValue, "Free", false);
}

// ── Settings ──────────────────────────────────────────────────────────────────

public record AppSettingsSnapshot(
    bool DarkTheme,
    bool StartMinimized,
    bool StartWithWindows,
    bool AutoUpdate,
    bool ShowTrayIcon,
    bool MinimizeToTray,
    string Language,
    int MonitoringIntervalMs,
    bool ShowNotifications,
    string TelegramToken,
    string TelegramChatId,
    string Version,
    string LastCheckUpdate
)
{
    // Дефолтный экземпляр
    public static AppSettingsSnapshot Default => new(
        DarkTheme: true,
        StartMinimized: false,
        StartWithWindows: false,
        AutoUpdate: true,
        ShowTrayIcon: true,
        MinimizeToTray: true,
        Language: "English",
        MonitoringIntervalMs: 1000,
        ShowNotifications: true,
        TelegramToken: "",
        TelegramChatId: "",
        Version: "1.0.0",
        LastCheckUpdate: "Never"
    );
}

// ── Scheduler ─────────────────────────────────────────────────────────────────

public record ScheduledTaskSnapshot(
    string Id,
    string Name,
    string Type,
    DateTime ExecuteAt,
    bool IsEnabled,
    bool IsRecurring,
    string Recurrence,
    int Interval,
    string CustomCommand,
    DateTime? LastExecuted
)
{
    public bool IsExpired => DateTime.Now > ExecuteAt && !IsRecurring;
}

// ── Game Mode ─────────────────────────────────────────────────────────────────

public record GameProfileSnapshot(
    string Name,
    string GameExecutablePath,
    bool SetHighPriority,
    bool ClearRamBeforeLaunch,
    bool EnableHighPerformancePowerPlan,
    bool DisableSysMain,
    bool DisableWindowsUpdates,
    bool DisableNotifications,
    bool DisableBackgroundApps,
    bool DisableAnimations,
    bool StopPrintSpooler,
    bool StopWindowsSearch,
    bool StopBluetooth,
    bool BlockWinKeys,
    bool BlockAltTab,
    bool BlockAltF4,
    bool AutoRestoreOnGameExit,
    bool DisableGameBar,
    bool DisableGameDvr,
    bool CloseBrowsersOnStart,
    bool StopXboxServices,
    bool DisableTransparency,
    IReadOnlyList<string> ProcessesToKeep,
    IReadOnlyList<string> ProcessesToKill
);

// ── Benchmark ─────────────────────────────────────────────────────────────────

public record BenchmarkResultSnapshot(
    string TestName,
    double Score,
    string Unit,
    string Details,
    double PercentageVsReference,
    string Rating,
    string Recommendation
);

// ── Startup ───────────────────────────────────────────────────────────────────

public record StartupItemSnapshot(
    string Name,
    string Path,
    string Location,
    bool IsEnabled,
    string Publisher,
    string Description,
    bool IsSigned
);