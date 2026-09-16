using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using SystemGuard.Desktop.Services;

namespace SystemGuard.Desktop.ViewModels;

public partial class DesktopIconRow : ObservableObject
{
    private readonly AppearancePacksService _service;
    public string Name { get; }
    [ObservableProperty] private bool _isVisible;

    public DesktopIconRow(AppearancePacksService service, string name, bool visible)
    {
        _service = service;
        Name = name;
        _isVisible = visible;
    }

    partial void OnIsVisibleChanged(bool value) => _service.SetDesktopIcon(Name, value);
}

public partial class AppearancePacksViewModel : ViewModelBase
{
    private readonly AppearancePacksService _service = new();
    private readonly DockService _dockService = new();
    private readonly WidgetsService _widgetsService = new();
    private bool _loading = true;

    // ── Droplet tabs: Style / Taskbar / Dock / Widgets (капля 132px) ────────
    [ObservableProperty] private string _selectedSection = "Style";
    [ObservableProperty] private bool _isStyleTab = true;
    [ObservableProperty] private bool _isTaskbarTab;
    [ObservableProperty] private bool _isDockTab;
    [ObservableProperty] private bool _isWidgetsTab;
    [ObservableProperty] private string _statusText = "Native theming — no system files modified";
    [ObservableProperty] private bool _isWorking;

    // ── Style: wallpaper ──
    [ObservableProperty] private string _selectedFit = "Fill";
    public List<string> Fits { get; } = new() { "Fill", "Fit", "Stretch", "Center", "Span" };

    // ── Style: cursors ──
    [ObservableProperty] private ObservableCollection<string> _cursorSchemes = new();
    [ObservableProperty] private string _selectedScheme = "Windows Default";
    [ObservableProperty] private int _cursorSize = 1;
    [ObservableProperty] private bool _cursorShadow;

    // ── Style: icons ──
    [ObservableProperty] private ObservableCollection<DesktopIconRow> _desktopIcons = new();
    [ObservableProperty] private int _iconSize = 32;

    // ── Style: explorer ──
    [ObservableProperty] private bool _hiddenFiles;
    [ObservableProperty] private bool _showExtensions = true;
    [ObservableProperty] private bool _launchToThisPC;

    // ── Style: window metrics ──
    [ObservableProperty] private int _captionHeight = 18;
    [ObservableProperty] private int _menuHeight = 18;
    [ObservableProperty] private int _scrollbarSize = 15;

    // ── Taskbar ──
    [ObservableProperty] private bool _centerIcons = true;
    [ObservableProperty] private bool _taskWidgets = true;
    [ObservableProperty] private bool _taskChat;
    [ObservableProperty] private bool _taskView = true;
    [ObservableProperty] private int _taskbarSize = 1;
    [ObservableProperty] private int _combineMode;
    [ObservableProperty] private bool _secondsClock;
    [ObservableProperty] private int _searchMode = 2;
    [ObservableProperty] private bool _transparency = true;
    [ObservableProperty] private bool _darkMode = true;

    // ── Dock ──
    [ObservableProperty] private bool _dockEnabled;
    [ObservableProperty] private bool _dockAutoHide;
    [ObservableProperty] private bool _dockMagnification = true;
    [ObservableProperty] private int _dockIconSize = 48;
    [ObservableProperty] private ObservableCollection<DockApp> _pinnedApps = new();
    [ObservableProperty] private ObservableCollection<DockApp> _runningApps = new();
    [ObservableProperty] private DockApp? _selectedRunning;
    [ObservableProperty] private string _newPinnedPath = "";

    // ── Widgets ──
    [ObservableProperty] private bool _widgetsEnabled;
    [ObservableProperty] private bool _showClock = true;
    [ObservableProperty] private bool _showSystem = true;
    [ObservableProperty] private bool _showWeather;
    [ObservableProperty] private string _city = "Moscow";
    [ObservableProperty] private int _weatherInterval = 300;

    public IRelayCommand<string> SelectTabCommand { get; }
    public IRelayCommand WallpaperCommand { get; }
    public IRelayCommand RestorePointCommand { get; }
    public IRelayCommand RestartExplorerCommand { get; }
    public IRelayCommand RestoreDefaultsCommand { get; }
    public IRelayCommand RefreshCommand { get; }
    public IRelayCommand SyncWindowsAccentCommand { get; }
    public IRelayCommand OpenThemesSettingsCommand { get; }
    public IRelayCommand BrowsePinnedCommand { get; }
    public IRelayCommand AddPinnedCommand { get; }
    public IRelayCommand<DockApp> RemovePinnedCommand { get; }
    public IRelayCommand<DockApp> LaunchPinnedCommand { get; }
    public IRelayCommand AddRunningCommand { get; }
    public IRelayCommand RefreshRunningCommand { get; }

    public AppearancePacksViewModel()
    {
        SelectTabCommand = new RelayCommand<string>(SelectTab);
        WallpaperCommand = new AsyncRelayCommand(PickWallpaperAsync);
        RestorePointCommand = new AsyncRelayCommand(CreateRestorePointAsync);
        RestartExplorerCommand = new RelayCommand(() =>
        {
            _service.RestartExplorer();
            StatusText = "Explorer restarting…";
        });
        RestoreDefaultsCommand = new RelayCommand(() =>
        {
            _service.RestoreTaskbarDefaults();
            LoadAll();
            StatusText = "Appearance settings restored";
        });
        RefreshCommand = new RelayCommand(() => { LoadAll(); StatusText = "Refreshed"; });
        SyncWindowsAccentCommand = new RelayCommand(SyncWindowsAccent);
        OpenThemesSettingsCommand = new RelayCommand(() => _service.OpenSettingsPage("ms-settings:themes"));
        BrowsePinnedCommand = new AsyncRelayCommand(BrowsePinnedAsync);
        AddPinnedCommand = new RelayCommand(AddPinned);
        RemovePinnedCommand = new RelayCommand<DockApp>(RemovePinned);
        LaunchPinnedCommand = new RelayCommand<DockApp>(a => { if (a != null) DockService.Launch(a.ExePath); });
        AddRunningCommand = new RelayCommand(AddRunning);
        RefreshRunningCommand = new RelayCommand(() =>
            RunningApps = new ObservableCollection<DockApp>(DockService.RunningWindowedApps()));

        LoadAll();
        _loading = false;
    }

    public override void OnActivated()
    {
        base.OnActivated();
        _loading = true;
        LoadAll();
        _loading = false;
    }

    private void SelectTab(string? tab)
    {
        if (string.IsNullOrEmpty(tab)) return;
        SelectedSection = tab;
        IsStyleTab = tab == "Style";
        IsTaskbarTab = tab == "Taskbar";
        IsDockTab = tab == "Dock";
        IsWidgetsTab = tab == "Widgets";
    }

    private void LoadAll()
    {
        // Style
        _selectedFit = _service.WallpaperFit; OnPropertyChanged(nameof(SelectedFit));
        CursorSchemes = new ObservableCollection<string>(_service.GetCursorSchemes());
        _selectedScheme = _service.CurrentCursorScheme(); OnPropertyChanged(nameof(SelectedScheme));
        _cursorSize = _service.CursorSize(); OnPropertyChanged(nameof(CursorSize));
        _cursorShadow = _service.CursorShadow(); OnPropertyChanged(nameof(CursorShadow));
        DesktopIcons = new ObservableCollection<DesktopIconRow>(
            _service.GetDesktopIcons().Select(kv => new DesktopIconRow(_service, kv.Key, kv.Value)));
        _iconSize = _service.IconSize(); OnPropertyChanged(nameof(IconSize));
        _hiddenFiles = _service.ShowHiddenFiles(); OnPropertyChanged(nameof(HiddenFiles));
        _showExtensions = _service.ShowExtensions(); OnPropertyChanged(nameof(ShowExtensions));
        _launchToThisPC = _service.LaunchToThisPC(); OnPropertyChanged(nameof(LaunchToThisPC));
        _captionHeight = _service.CaptionHeight(); OnPropertyChanged(nameof(CaptionHeight));
        _menuHeight = _service.MenuHeight(); OnPropertyChanged(nameof(MenuHeight));
        _scrollbarSize = _service.ScrollbarSize(); OnPropertyChanged(nameof(ScrollbarSize));

        // Taskbar
        var t = _service.GetTaskbarStates();
        _centerIcons = t["CenterIcons"] != 0; _taskWidgets = t["Widgets"] != 0;
        _taskChat = t["Chat"] != 0; _taskView = t["TaskView"] != 0;
        _taskbarSize = t["TaskbarSize"]; _combineMode = t["Combine"];
        _secondsClock = t["SecondsClock"] != 0; _searchMode = t["SearchMode"];
        foreach (var n in new[] { nameof(CenterIcons), nameof(TaskWidgets), nameof(TaskChat), nameof(TaskView),
                                   nameof(TaskbarSize), nameof(CombineMode), nameof(SecondsClock), nameof(SearchMode) })
            OnPropertyChanged(n);
        var th = _service.GetThemeStates();
        _transparency = th["Transparency"] != 0; _darkMode = th["DarkMode"] != 0;
        OnPropertyChanged(nameof(Transparency)); OnPropertyChanged(nameof(DarkMode));

        // Dock
        var ds = _dockService.LoadSettings();
        _dockEnabled = ds.Enabled; _dockAutoHide = ds.AutoHide;
        _dockMagnification = ds.Magnification; _dockIconSize = ds.IconSize;
        foreach (var n in new[] { nameof(DockEnabled), nameof(DockAutoHide), nameof(DockMagnification), nameof(DockIconSize) })
            OnPropertyChanged(n);
        PinnedApps = new ObservableCollection<DockApp>(_dockService.LoadApps());
        RunningApps = new ObservableCollection<DockApp>(DockService.RunningWindowedApps());

        // Widgets
        var ws = _widgetsService.Load();
        _widgetsEnabled = ws.Enabled; _showClock = ws.ShowClock; _showSystem = ws.ShowSystem;
        _showWeather = ws.ShowWeather; _city = ws.City; _weatherInterval = ws.RefreshSeconds;
        foreach (var n in new[] { nameof(WidgetsEnabled), nameof(ShowClock), nameof(ShowSystem),
                                   nameof(ShowWeather), nameof(City), nameof(WeatherInterval) })
            OnPropertyChanged(n);
    }

    // ── Style handlers ──
    partial void OnSelectedFitChanged(string value) => StatusText = $"Wallpaper fit: {value} (applies to next wallpaper)";
    partial void OnSelectedSchemeChanged(string value)
    {
        if (_loading) return;
        StatusText = _service.ApplyCursorScheme(value) ? $"Cursor scheme: {value}" : "Cannot apply scheme";
    }
    partial void OnCursorSizeChanged(int value)
    {
        if (_loading) return;
        _service.SetCursorSize(value);
        StatusText = $"Cursor size: {value} (relogin to fully apply)";
    }
    partial void OnCursorShadowChanged(bool value)
    {
        if (_loading) return;
        _service.SetCursorShadow(value);
        StatusText = value ? "Cursor shadow on" : "Cursor shadow off";
    }
    partial void OnIconSizeChanged(int value)
    {
        if (_loading) return;
        _service.SetIconSize(value);
        StatusText = $"Icon size: {value}px (restart Explorer to apply)";
    }
    partial void OnHiddenFilesChanged(bool value)
    {
        if (_loading) return;
        _service.SetHiddenFiles(value);
        StatusText = value ? "Hidden files shown" : "Hidden files hidden";
    }
    partial void OnShowExtensionsChanged(bool value)
    {
        if (_loading) return;
        _service.SetExtensions(value);
        StatusText = value ? "File extensions shown" : "File extensions hidden";
    }
    partial void OnLaunchToThisPCChanged(bool value)
    {
        if (_loading) return;
        _service.SetLaunchToThisPC(value);
        StatusText = value ? "Explorer opens This PC" : "Explorer opens Quick Access";
    }
    partial void OnCaptionHeightChanged(int value)
    {
        if (_loading) return;
        _service.SetCaptionHeight(value);
        StatusText = $"Title bars: {value}px (relogin for full effect)";
    }
    partial void OnMenuHeightChanged(int value)
    {
        if (_loading) return;
        _service.SetMenuHeight(value);
        StatusText = $"Menus: {value}px";
    }
    partial void OnScrollbarSizeChanged(int value)
    {
        if (_loading) return;
        _service.SetScrollbarSize(value);
        StatusText = $"Scrollbars: {value}px";
    }

    // ── Taskbar handlers ──
    partial void OnCenterIconsChanged(bool v) { if (_loading) return; _service.SetTaskbarCenter(v); StatusText = "Alignment updated — restart Explorer"; }
    partial void OnTaskWidgetsChanged(bool v) { if (_loading) return; _service.SetWidgets(v); StatusText = "Widgets toggled — restart Explorer"; }
    partial void OnTaskChatChanged(bool v) { if (_loading) return; _service.SetChat(v); StatusText = "Chat toggled — restart Explorer"; }
    partial void OnTaskViewChanged(bool v) { if (_loading) return; _service.SetTaskView(v); StatusText = "Task View toggled"; }
    partial void OnTaskbarSizeChanged(int v) { if (_loading) return; _service.SetTaskbarSize(v); StatusText = "Taskbar size updated — restart Explorer"; }
    partial void OnCombineModeChanged(int v) { if (_loading) return; _service.SetCombine(v); StatusText = "Button combining updated — restart Explorer"; }
    partial void OnSecondsClockChanged(bool v) { if (_loading) return; _service.SetSecondsClock(v); StatusText = "Clock seconds toggled — restart Explorer"; }
    partial void OnSearchModeChanged(int v) { if (_loading) return; _service.SetSearchMode(v); StatusText = "Search box updated — restart Explorer"; }
    partial void OnTransparencyChanged(bool v) { if (_loading) return; _service.SetTransparency(v); StatusText = "Transparency toggled"; }
    partial void OnDarkModeChanged(bool v) { if (_loading) return; _service.SetDarkMode(v); StatusText = "Theme toggled"; }

    // ── Dock handlers ──
    partial void OnDockEnabledChanged(bool value)
    {
        if (_loading) return;
        SaveDockSettings();
        if (value) OverlayManager.ShowDock(); else OverlayManager.HideDock();
        StatusText = value ? "Dock shown" : "Dock hidden";
    }
    partial void OnDockAutoHideChanged(bool value) { if (_loading) return; SaveDockSettings(); OverlayManager.RefreshDock(); }
    partial void OnDockMagnificationChanged(bool value) { if (_loading) return; SaveDockSettings(); OverlayManager.RefreshDock(); }
    partial void OnDockIconSizeChanged(int value) { if (_loading) return; SaveDockSettings(); OverlayManager.RefreshDock(); }

    private void SaveDockSettings()
    {
        _dockService.SaveSettings(new DockSettings
        {
            Enabled = DockEnabled, AutoHide = DockAutoHide,
            Magnification = DockMagnification, IconSize = DockIconSize
        });
    }

    private void AddPinned()
    {
        if (string.IsNullOrWhiteSpace(NewPinnedPath)) return;
        var path = NewPinnedPath.Trim().Trim('"');
        if (!System.IO.File.Exists(path)) { StatusText = "File not found"; return; }
        var apps = _dockService.LoadApps();
        if (apps.Any(a => a.ExePath.Equals(path, System.StringComparison.OrdinalIgnoreCase))) return;
        apps.Add(new DockApp { Name = System.IO.Path.GetFileNameWithoutExtension(path), ExePath = path });
        _dockService.SaveApps(apps);
        PinnedApps = new ObservableCollection<DockApp>(apps);
        NewPinnedPath = "";
        OverlayManager.RefreshDock();
        StatusText = "Pinned to dock";
    }

    private void AddRunning()
    {
        if (SelectedRunning == null) return;
        var apps = _dockService.LoadApps();
        if (apps.Any(a => a.ExePath.Equals(SelectedRunning.ExePath, System.StringComparison.OrdinalIgnoreCase))) return;
        apps.Add(new DockApp { Name = SelectedRunning.Name, ExePath = SelectedRunning.ExePath });
        _dockService.SaveApps(apps);
        PinnedApps = new ObservableCollection<DockApp>(apps);
        OverlayManager.RefreshDock();
        StatusText = $"{SelectedRunning.Name} pinned";
    }

    private void RemovePinned(DockApp? app)
    {
        if (app == null) return;
        var apps = _dockService.LoadApps();
        apps.RemoveAll(a => a.ExePath.Equals(app.ExePath, System.StringComparison.OrdinalIgnoreCase));
        _dockService.SaveApps(apps);
        PinnedApps = new ObservableCollection<DockApp>(apps);
        OverlayManager.RefreshDock();
    }

    private async Task BrowsePinnedAsync()
    {
        var top = GetTopLevel();
        if (top?.StorageProvider == null) { StatusText = "Cannot open file dialog"; return; }
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose app to pin",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("Apps") { Patterns = new[] { "*.exe", "*.lnk" } } }
        });
        if (files.Count == 0) return;
        NewPinnedPath = files[0].TryGetLocalPath() ?? "";
        if (!string.IsNullOrEmpty(NewPinnedPath)) AddPinned();
    }

    // ── Widgets handlers ──
    partial void OnWidgetsEnabledChanged(bool value)
    {
        if (_loading) return;
        SaveWidgetsSettings();
        if (value) OverlayManager.ShowWidgets(); else OverlayManager.HideWidgets();
        StatusText = value ? "Widgets shown" : "Widgets hidden";
    }
    partial void OnShowClockChanged(bool _) { if (_loading) return; SaveWidgetsSettings(); OverlayManager.RefreshWidgets(); }
    partial void OnShowSystemChanged(bool _) { if (_loading) return; SaveWidgetsSettings(); OverlayManager.RefreshWidgets(); }
    partial void OnShowWeatherChanged(bool _) { if (_loading) return; SaveWidgetsSettings(); OverlayManager.RefreshWidgets(); }
    partial void OnCityChanged(string _) { if (_loading) return; SaveWidgetsSettings(); OverlayManager.RefreshWidgets(); }
    partial void OnWeatherIntervalChanged(int _) { if (_loading) return; SaveWidgetsSettings(); }

    private void SaveWidgetsSettings()
    {
        _widgetsService.Save(new WidgetsSettings
        {
            Enabled = WidgetsEnabled, ShowClock = ShowClock, ShowSystem = ShowSystem,
            ShowWeather = ShowWeather, City = City,
            RefreshSeconds = System.Math.Clamp(WeatherInterval, 60, 3600)
        });
    }

    private void SyncWindowsAccent()
    {
        try
        {
            var app = Avalonia.Application.Current;
            if (app?.Resources.TryGetResource("AccentColor", app.ActualThemeVariant, out var res) == true
                && res is Avalonia.Media.Color c)
            {
                var hex = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
                StatusText = _service.SetWindowsAccent(hex)
                    ? $"Windows accent synced: {hex}" : "Could not sync accent";
            }
            else StatusText = "Accent color not found";
        }
        catch (System.Exception ex) { StatusText = $"Accent sync failed: {ex.Message}"; }
    }

    // ── Shared ──
    private async Task CreateRestorePointAsync()
    {
        IsWorking = true;
        StatusText = "Creating restore point… (may need Administrator)";
        StatusText = await _service.CreateRestorePointAsync();
        IsWorking = false;
    }

    private async Task PickWallpaperAsync()
    {
        var top = GetTopLevel();
        if (top?.StorageProvider == null) { StatusText = "Cannot open file dialog"; return; }
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose wallpaper",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Images") { Patterns = new[] { "*.jpg", "*.jpeg", "*.png", "*.bmp" } }
            }
        });
        if (files.Count == 0) return;
        var path = files[0].TryGetLocalPath();
        if (string.IsNullOrEmpty(path)) return;
        StatusText = _service.SetWallpaper(path, SelectedFit) ? "Wallpaper applied" : "Could not apply wallpaper";
    }

    private Avalonia.Controls.TopLevel? GetTopLevel()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return null;
        return TopLevel.GetTopLevel(desktop.MainWindow);
    }
}
