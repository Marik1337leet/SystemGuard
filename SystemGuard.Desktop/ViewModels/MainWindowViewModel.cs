using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;
using SystemGuard.Desktop.Services;

namespace SystemGuard.Desktop.ViewModels;

public partial class MainWindowViewModel : ViewModelBase, IDisposable
{
    // ── Lazy VM ───────────────────────────────────────────────────────────────
    private DashboardViewModel? _dashboardVM;
    private DiskCleanupViewModel? _diskCleanupVM;
    private ProcessManagerViewModel? _processManagerVM;
    private PerformanceViewModel? _perfVM;
    private GameModeViewModel? _gameModeVM;
    private StartupViewModel? _startupVM;
    private SettingsViewModel? _settingsVM;
    private TelegramViewModel? _telegramVM;
    private SchedulerViewModel? _schedulerVM;
    private NetworkViewModel? _networkVM;
    private LicenseViewModel? _licenseVM;
    private ColorPickerViewModel? _colorPickerVM;
    private DeepUninstallViewModel? _deepUninstallVM;
    private AppearancePacksViewModel? _appearanceVM;
    private SecurityViewModel? _securityVM;

    private readonly LicenseService _licenseService = new();
    private WebSocketServer? _wsServer;

    [ObservableProperty] private ViewModelBase? _currentView;
    [ObservableProperty] private string _currentViewTitle = "DASHBOARD";
    [ObservableProperty] private ObservableCollection<MenuItemViewModel> _menuItems = new();
    [ObservableProperty] private string _licenseInfo = "Free";
    [ObservableProperty] private string _licenseDaysLeft = "";
    [ObservableProperty] private bool _isProLicense;
    [ObservableProperty] private string _licenseBadgeChar = "F";

    public string AppVersion { get; } = GetAppVersion();

    public MainWindowViewModel()
    {
        InitializeMenu();
        LocalizationService.Instance.LanguageChanged += _ => ApplyMenuLanguage();
        // Применяем сохранённый язык при старте (раньше настройка ни на что не влияла)
        try { LocalizationService.Instance.SetLanguage(new SettingsService().Load().Language); } catch { }
        ApplyMenuLanguage();
        RefreshLicenseInfo();
        NavigateTo("dashboard");
        StartWebSocket();
        OverlayManager.RestoreAtStartup();
    }

    private void ApplyMenuLanguage()
    {
        var loc = LocalizationService.Instance;
        foreach (var item in MenuItems)
            item.Title = item.ViewType switch
            {
                "dashboard" => loc["Dashboard"],
                "processes" => loc["Processes"],
                "startup" => loc["Startup"],
                "cleanup" => loc["Cleanup"],
                "uninstall" => loc["Uninstaller"],
                "gaming" => loc["Gaming"],
                "performance" => loc["Performance"],
                "network" => loc["Network"],
                "security" => loc["Security"],
                "settings" => loc["Settings"],
                // Совместимость
                "telegram" => loc["Telegram"],
                "scheduler" => loc["Scheduler"],
                "license" => loc["License"],
                "colors" => loc["Colors"],
                "appearance" => loc["Appearance"],
                _ => item.Title
            };
        var current = MenuItems.FirstOrDefault(m => m.IsSelected);
        if (current != null) CurrentViewTitle = current.Title.ToUpper();
    }

    // ── License ───────────────────────────────────────────────────────────────

    private void RefreshLicenseInfo()
    {
        var lic = _licenseService.CurrentLicense;
        IsProLicense = lic.IsValid && lic.Tier != "Free" && !lic.IsExpired;

        if (lic.IsValid && !lic.IsExpired)
        {
            LicenseInfo = lic.Tier;
            LicenseDaysLeft = lic.Tier == "Free"
                ? $"{lic.DaysLeft} days trial"
                : $"{lic.DaysLeft} days left";
            LicenseBadgeChar = lic.Tier == "Enterprise" ? "E"
                : lic.Tier == "Pro" ? "P" : "T";
        }
        else
        {
            LicenseInfo = "Free";
            LicenseDaysLeft = "Upgrade to Pro";
            LicenseBadgeChar = "F";
        }
    }

    // ── WebSocket ─────────────────────────────────────────────────────────────

    private void StartWebSocket()
    {
        try { _wsServer = new WebSocketServer(); _wsServer.Start(8888); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[WS] {ex.Message}"); }
    }

    // ── Menu ──────────────────────────────────────────────────────────────────

    private void InitializeMenu()
    {
        // 9 пунктов: Telegram и Scheduler живут в Настройках,
        // Лицензия/Цвета/Оформление — тоже там. Остальное раздельно, чтобы не было пусто.
        var items = new (string Icon, string Title, string ViewType)[]
        {
            ("M3 13h8V3H3v10zm0 8h8v-6H3v6zm10 0h8V11h-8v10zm0-18v6h8V3h-8z",
             "Dashboard", "dashboard"),

            ("M20 2H4c-1.1 0-2 .9-2 2v12c0 1.1.9 2 2 2h14l4 4V4c0-1.1-.9-2-2-2zm-2 12H6v-2h12v2zm0-3H6V9h12v2zm0-3H6V6h12v2z",
             "Processes", "processes"),

            ("M19 13h-6v6h-2v-6H5v-2h6V5h2v6h6v2z",
             "Startup", "startup"),

            ("M6 19c0 1.1.9 2 2 2h8c1.1 0 2-.9 2-2V7H6v12zM19 4h-3.5l-1-1h-5l-1 1H5v2h14V4z",
             "Cleanup", "cleanup"),

            ("M20.37 8.91l-1-1.73-2.32.67-1.56-1.56.67-2.32-1.73-1-1.18 1.94-2.32-.01L9.76 3l-1.73 1 .67 2.32-1.56 1.56L4.82 7.2l-1 1.73 1.94 1.17-.01 2.32L3.82 13.8l1 1.73 2.32-.67 1.56 1.56-.67 2.32 1.73 1 1.18-1.94 2.32.01 1.17 1.94 1.73-1-.67-2.32 1.56-1.56 2.32.67 1-1.73-1.94-1.17.01-2.32 1.94-1.17zM12 15.5c-1.93 0-3.5-1.57-3.5-3.5s1.57-3.5 3.5-3.5 3.5 1.57 3.5 3.5-1.57 3.5-3.5 3.5z",
             "Uninstaller", "uninstall"),

            ("M21 6H3c-1.1 0-2 .9-2 2v8c0 1.1.9 2 2 2h18c1.1 0 2-.9 2-2V8c0-1.1-.9-2-2-2zm0 10H3V8h18v8zM6 15h2v-2h2v-2H8V9H6v2H4v2h2z",
             "Gaming", "gaming"),

            ("M13 3h-2v10h2V3zm4.83 2.17l-1.42 1.42C17.99 7.86 19 9.81 19 12c0 3.87-3.13 7-7 7s-7-3.13-7-7c0-2.19 1.01-4.14 2.59-5.42L6.17 5.17C4.23 6.82 3 9.26 3 12c0 4.97 4.03 9 9 9s9-4.03 9-9c0-2.74-1.23-5.18-3.17-6.83z",
             "Performance", "performance"),

            ("M1 9l2 2c4.97-4.97 13.03-4.97 18 0l2-2C16.93 2.93 7.08 2.93 1 9zm8 8l3 3 3-3a4.237 4.237 0 0 0-6 0zm-4-4l2 2a7.074 7.074 0 0 1 10 0l2-2C15.14 9.14 8.87 9.14 5 13z",
             "Network", "network"),

            ("M12 1L3 5v6c0 5.55 3.84 10.74 9 12 5.16-1.26 9-6.45 9-12V5l-9-4zm0 10.99h7c-.53 4.12-3.28 7.79-7 8.94V12H5V6.3l7-3.11v8.8z",
             "Security", "security"),

            ("M19.14 12.94c.04-.3.06-.61.06-.94 0-.32-.02-.64-.07-.94l2.03-1.58c.18-.14.23-.41.12-.61l-1.92-3.32c-.12-.22-.37-.29-.59-.22l-2.39.96c-.5-.38-1.03-.7-1.62-.94L14.4 2.81c-.04-.24-.24-.41-.48-.41h-3.84c-.24 0-.43.17-.47.41L9.25 5.35C8.66 5.59 8.12 5.92 7.63 6.29L5.24 5.33c-.22-.08-.47 0-.59.22L2.74 8.87c-.12.22-.07.47.12.61l2.03 1.58c-.05.3-.07.62-.07.94s.02.64.07.94l-2.03 1.58c-.18.14-.23.41-.12.61l1.92 3.32c.12.22.37.29.59.22l2.39-.96c.5.38 1.03.7 1.62.94l.36 2.54c.05.24.24.41.48.41h3.84c.24 0 .44-.17.47-.41l.36-2.54c.59-.24 1.13-.56 1.62-.94l2.39.96c.22.08.47 0 .59-.22l1.92-3.32c.12-.22.07-.47-.12-.61L19.14 12.94z",
             "Settings", "settings"),
        };

        foreach (var (icon, title, viewType) in items)
        {
            MenuItems.Add(new MenuItemViewModel
            {
                Icon = icon,
                Title = title,
                ViewType = viewType,
                NavigationAction = NavigateTo
            });
        }
    }

    // ── Navigation ────────────────────────────────────────────────────────────

    public void NavigateTo(string viewType)
    {
        (_currentView as ViewModelBase)?.OnDeactivated();

        foreach (var item in MenuItems)
            item.IsSelected = item.ViewType == viewType;

        var menuItem = MenuItems.FirstOrDefault(m => m.ViewType == viewType);
        CurrentViewTitle = menuItem?.Title.ToUpper() ?? viewType.ToUpper();

        ViewModelBase next = viewType switch
        {
            "dashboard" => _dashboardVM ??= new DashboardViewModel(),
            "processes" => _processManagerVM ??= new ProcessManagerViewModel(),
            "startup" => _startupVM ??= new StartupViewModel(),
            "cleanup" => _diskCleanupVM ??= new DiskCleanupViewModel(),
            "uninstall" => _deepUninstallVM ??= new DeepUninstallViewModel(),
            "gaming" => _gameModeVM ??= new GameModeViewModel(new GameModeService(), new PowerService()),
            "performance" => _perfVM ??= new PerformanceViewModel(),
            "network" => _networkVM ??= new NetworkViewModel(),
            "security" => _securityVM ??= new SecurityViewModel(),
            "settings" => _settingsVM ??= new SettingsViewModel(),
            // Совместимость
            "telegram" => _telegramVM ??= new TelegramViewModel(),
            "scheduler" => _schedulerVM ??= new SchedulerViewModel(),
            "license" => _licenseVM ??= new LicenseViewModel(),
            "colors" => _colorPickerVM ??= new ColorPickerViewModel(),
            "appearance" => _appearanceVM ??= new AppearancePacksViewModel(),
            _ => _dashboardVM ??= new DashboardViewModel()
        };

        CurrentView = next;
        next.OnActivated();

        // Прокидываем окно в VM которым нужен файловый диалог
        if (next is GameModeViewModel gm && _ownerWindow != null)
            gm.OwnerWindow = _ownerWindow;
        if (next is SettingsViewModel sv && _ownerWindow != null)
            sv.OwnerWindow = _ownerWindow;
    }

    // ── Owner window ref (для файловых диалогов) ─────────────────────────────

    private Avalonia.Controls.Window? _ownerWindow;

    public void SetOwnerWindow(Avalonia.Controls.Window window)
    {
        _ownerWindow = window;
    }

    // ── Version ───────────────────────────────────────────────────────────────

    private static string GetAppVersion()
    {
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        return v != null ? $"v{v.Major}.{v.Minor}.{v.Build}" : "v1.0.0";
    }

    // ── Cleanup ───────────────────────────────────────────────────────────────

    public void Shutdown()
    {
        (_currentView as ViewModelBase)?.OnDeactivated();
        _dashboardVM?.OnDeactivated();
        _networkVM?.OnDeactivated();
        _processManagerVM?.OnDeactivated();
        _diskCleanupVM?.OnDeactivated();
        _settingsVM?.OnDeactivated();
        _wsServer?.Stop();
        _wsServer?.Dispose();
        OverlayManager.Shutdown();
    }

    public void Dispose() => Shutdown();
}
