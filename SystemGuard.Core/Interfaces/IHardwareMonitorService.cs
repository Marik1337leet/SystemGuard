// SystemGuard.Core — интерфейсы сервисов
// Desktop проект ссылается на Core через ProjectReference

using System.Threading;

namespace SystemGuard.Core.Interfaces;

// ── Hardware ──────────────────────────────────────────────────────────────────

public interface IHardwareMonitorService
{
    event Action<HardwareSnapshot>? OnUpdated;
    event Action<string>? OnError;
    void Start(int intervalMs = 1000);
    void Stop();
    bool IsRunning { get; }
    bool IsElevated { get; }
}

// ── Process ───────────────────────────────────────────────────────────────────

public interface IProcessService
{
    bool IsElevated { get; }
    IReadOnlyList<ProcessSnapshot> GetProcesses();
    ProcessActionResult KillProcess(int pid);
    ProcessActionResult KillProcessForce(int pid);
    ProcessActionResult SuspendProcess(int pid);
    ProcessActionResult ResumeProcess(int pid);
    string GetProcessPath(int pid);
    string GetCommandLine(int pid);
    Task<string> CalculateMD5Async(string filePath);
}

public record ProcessActionResult(bool Success, string Message)
{
    public static ProcessActionResult Ok(string message) => new(true, message);
    public static ProcessActionResult Fail(string message) => new(false, message);
}

// ── Elevation ─────────────────────────────────────────────────────────────────

public interface IElevationService
{
    bool IsElevated { get; }
    void RestartAsAdministrator();
}

// ── Network ───────────────────────────────────────────────────────────────────

public interface INetworkService
{
    IReadOnlyList<AdapterSnapshot> GetAdapters();
    NetworkTrafficSnapshot GetTraffic();
    Task<double> TestLatencyAsync(string host = "8.8.8.8");
    Task<string> GetPublicIpAsync();
    void FlushDns();
    void ResetNetwork();
}

// ── Cleanup ───────────────────────────────────────────────────────────────────

public interface ICleanupService
{
    Task<CleanupResult> CleanAllAsync();
    CleanupResult OptimizeRam();
}

// ── Disk ──────────────────────────────────────────────────────────────────────

public interface IDiskService
{
    IReadOnlyList<DiskSnapshot> GetDrives();

    Task<IReadOnlyList<FolderSizeSnapshot>> AnalyzeFoldersAsync(
        string root,
        int maxDepth = 3,
        IProgress<FolderSizeSnapshot>? onFolderFound = null,
        CancellationToken ct = default);

    Task<IReadOnlyList<LargeFileSnapshot>> FindLargeFilesAsync(
        string root,
        long minBytes = 100 * 1024 * 1024,
        IProgress<LargeFileSnapshot>? onFileFound = null,
        CancellationToken ct = default);

    Task<IReadOnlyList<DuplicateGroup>> FindDuplicatesAsync(
        string root,
        IProgress<DuplicateGroup>? onGroupFound = null,
        IProgress<string>? onStatus = null,
        CancellationToken ct = default);
}

// ── Power ─────────────────────────────────────────────────────────────────────

public interface IPowerService
{
    IReadOnlyList<PowerPlanSnapshot> GetPowerPlans();
    void SetActivePlan(string guid);
    BatterySnapshot? GetBattery();
    void Shutdown(int delaySec = 0);
    void Restart(int delaySec = 0);
    void Sleep();
    void Hibernate();
    void Lock();
}

// ── License ───────────────────────────────────────────────────────────────────

public interface ILicenseService
{
    LicenseSnapshot CurrentLicense { get; }
    bool ActivateTrial();
    (bool Success, string Message) ActivatePro(string key);
    void Deactivate();
    bool IsFeatureAvailable(string feature);
    event Action<LicenseSnapshot>? OnLicenseChanged;
}

// ── Settings ──────────────────────────────────────────────────────────────────

public interface ISettingsService
{
    AppSettingsSnapshot Load();
    void Save(AppSettingsSnapshot settings);
    void Export(string path);
    void Import(string path);
    void Reset();
    event Action<AppSettingsSnapshot>? OnSettingsChanged;
}

// ── Localization ──────────────────────────────────────────────────────────────

public interface ILocalizationService
{
    string CurrentLanguage { get; }
    void SetLanguage(string lang);
    string Get(string key, string defaultValue = "");
    IReadOnlyList<string> GetAvailableLanguages();
    event Action<string>? OnLanguageChanged;
}

// ── Scheduler ─────────────────────────────────────────────────────────────────

public interface ISchedulerService
{
    event Action<ScheduledTaskSnapshot>? OnTaskExecuted;
    IReadOnlyList<ScheduledTaskSnapshot> GetTasks();
    void AddTask(ScheduledTaskSnapshot task);
    void RemoveTask(string id);
    void ToggleTask(string id);
    void Start();
    void Stop();
}

// ── Telegram Bot ──────────────────────────────────────────────────────────────

public interface ITelegramBotService
{
    bool IsConfigured { get; }
    bool IsListening { get; }
    void Configure(string token, string chatId);
    void StartListening();
    void StopListening();
    Task<string> SendMessageAsync(string text);
    Task<string> SendScreenshotAsync();
    Task<string> ExecuteCommandAsync(string cmd);
    Task<string> GetSystemStatusAsync();
    event Action<string, string>? OnCommandReceived;
}

// ── Startup ───────────────────────────────────────────────────────────────────

public interface IStartupService
{
    IReadOnlyList<StartupItemSnapshot> GetStartupItems();
    void DisableItem(StartupItemSnapshot item);
    void EnableItem(StartupItemSnapshot item);
    long GetStartupImpactBytes();
}

// ── Game Mode ─────────────────────────────────────────────────────────────────

public interface IGameModeService
{
    bool IsActive { get; }
    GameProfileSnapshot? ActiveProfile { get; }
    bool IsGameProcessRunning { get; }
    event Action? OnAutoRestored;
    Task EnableAsync(GameProfileSnapshot profile);
    Task DisableAsync();
    IReadOnlyList<GameProfileSnapshot> GetDefaultProfiles();
    GameProfileSnapshot CreateCustomProfile(string name, string executablePath);
    void SaveProfile(GameProfileSnapshot profile);
    void DeleteProfile(string name);
}

// ── System Tweaks ─────────────────────────────────────────────────────────────

public enum TweakCategory { Startup, Devices, Services, Visuals, System, Network, Registry, Misc }

public record TweakDefinition(
    string Id,
    string Name,
    string Description,
    TweakCategory Category,
    bool IsDangerous,
    bool RequiresAdmin,
    bool RequiresRestart,
    bool IsImplemented
);

public record TweakActionResult(bool Success, string Message)
{
    public static TweakActionResult Ok(string message) => new(true, message);
    public static TweakActionResult Fail(string message) => new(false, message);
}

public interface ITweaksService
{
    IReadOnlyList<TweakDefinition> GetAllTweaks();
    bool IsApplied(string id);
    TweakActionResult Apply(string id);
    TweakActionResult Revert(string id);
}

// ── Benchmark ─────────────────────────────────────────────────────────────────

public interface IBenchmarkService
{
    Task<BenchmarkResultSnapshot> BenchmarkCpuSingleCoreAsync();
    Task<BenchmarkResultSnapshot> BenchmarkCpuMultiCoreAsync();
    Task<BenchmarkResultSnapshot> BenchmarkMemoryBandwidthAsync();
    Task<BenchmarkResultSnapshot> BenchmarkMemoryLatencyAsync();
    Task<BenchmarkResultSnapshot> BenchmarkDiskSequentialWriteAsync();
    Task<BenchmarkResultSnapshot> BenchmarkDiskSequentialReadAsync();
    Task<BenchmarkResultSnapshot> BenchmarkDiskRandomAccessAsync();
    Task<BenchmarkResultSnapshot> BenchmarkNetworkSpeedAsync();
    string GetOverallRating(IReadOnlyList<BenchmarkResultSnapshot> results);
    string GetSystemSummary(IReadOnlyList<BenchmarkResultSnapshot> results);
}

// ── Google Integration ────────────────────────────────────────────────────────

public interface IGoogleIntegrationService
{
    bool IsSignedIn { get; }
    string? UserEmail { get; }
    string? UserName { get; }
    string? AvatarUrl { get; }
    Task<bool> SignInAsync();
    Task SignOutAsync();
    Task<bool> UploadToDriveAsync(string localPath, string driveFolder = "SystemGuard");
    Task<bool> DownloadFromDriveAsync(string driveFileId, string localPath);
    Task<IReadOnlyList<DriveFileInfo>> ListDriveFilesAsync(string folder = "SystemGuard");
    event Action<bool>? OnSignInStateChanged;
}

public record DriveFileInfo(
    string Id,
    string Name,
    long SizeBytes,
    DateTime ModifiedAt,
    string MimeType
);

// ── Quick Optimizer ───────────────────────────────────────────────────────────

public interface IQuickOptimizerService
{
    Task<QuickOptimizationResult> OptimizeAsync();
}

public record QuickOptimizationResult(
    long RamFreedBytes,
    long TempCleanedBytes,
    int ProcessesStopped,
    int ServicesOptimized,
    TimeSpan Duration,
    string Summary,
    IReadOnlyList<string> StoppedProcessNames
);

// ── Uninstall Manager ─────────────────────────────────────────────────────────

public interface IUninstallService
{
    IReadOnlyList<UninstallEntry> GetInstalledApps();
    Task<bool> UninstallAsync(string appId);
    Task<bool> UninstallSilentAsync(string appId);
}

public record UninstallEntry(
    string Id,
    string DisplayName,
    string Publisher,
    string InstallLocation,
    string UninstallString,
    long EstimatedSizeBytes,
    DateTime InstallDate,
    string Version,
    bool IsSystemComponent
);