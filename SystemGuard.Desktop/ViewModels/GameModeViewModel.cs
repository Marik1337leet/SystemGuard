using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DynamicData;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using SystemGuard.Core.Interfaces;
using SystemGuard.Desktop.Models;
using SystemGuard.Desktop.Services;

namespace SystemGuard.Desktop.ViewModels;

/// <summary>
/// Обёртка над профилем для UI-списка слева: несёт собственное состояние
/// "выбран ли этот профиль", чтобы кнопка красила саму себя, а не полагалась
/// на встроенную подсветку контейнера ListBoxItem (которая красит весь ряд,
/// а не только кнопку — это и было "окрашивание вокруг").
/// </summary>
public partial class ProfileRowViewModel : ObservableObject
{
    public GameProfileSnapshot Profile { get; }
    [ObservableProperty] private bool _isSelected;

    public ProfileRowViewModel(GameProfileSnapshot profile, bool isSelected)
    {
        Profile = profile;
        _isSelected = isSelected;
    }
}

public partial class GameModeViewModel : ViewModelBase
{
    private readonly GameModeService _gameModeService;
    private readonly PowerService _powerService;
    private readonly IFileDialogService _fileDialog;
    private readonly IElevationService _elevation;
    private readonly GameProfileStorageService _storage;
    private readonly DispatcherTimer _elapsedTimer;
    private readonly HardwareMonitorService _liveMonitor = new();
    private readonly GameAutoDetectService _autoDetect = new();
    private DateTime? _enabledAt;
    private bool _autoEnabled;

    [ObservableProperty] private ObservableCollection<GameProfileSnapshot> _profiles = new();
    [ObservableProperty] private ObservableCollection<ProfileRowViewModel> _profileRows = new();
    [ObservableProperty] private ProfileRowViewModel? _selectedRow;
    [ObservableProperty] private GameProfileSnapshot? _selectedProfile;
    [ObservableProperty] private bool _isGameModeActive;
    [ObservableProperty] private bool _isGameRunning;
    [ObservableProperty] private string _statusText = "Ready";
    [ObservableProperty] private string _elapsedText = "";
    [ObservableProperty] private string _newProcessToKeep = "";
    [ObservableProperty] private string _newProcessToKill = "";
    [ObservableProperty] private string _renameText = "";
    [ObservableProperty] private bool _isDeleteConfirming;
    [ObservableProperty] private ObservableCollection<PowerPlanInfo> _powerPlans = new();
    [ObservableProperty] private string _activePowerPlanName = "Balanced";
    [ObservableProperty] private string _toggleButtonText = "ENABLE GAME MODE";
    [ObservableProperty] private bool _isElevated;
    [ObservableProperty] private double _cpuNow;
    [ObservableProperty] private double _ramNow;
    [ObservableProperty] private string _gameReport = "";
    [ObservableProperty] private ObservableCollection<GameEntry> _detectedGames = new();
    [ObservableProperty] private bool _isScanningLibrary;
    [ObservableProperty] private bool _autoDetectEnabled = true;
    public bool HasDetectedGames => DetectedGames.Count > 0;

    [ObservableProperty] private string _activeSection = "Profiles";

    public bool IsProfilesSection => ActiveSection == "Profiles";
    public bool IsTweaksSection => ActiveSection == "Tweaks";
    public string DeleteButtonText => IsDeleteConfirming ? "Confirm Delete?" : "Delete";
    public string ElevationLabel => IsElevated ? "Administrator" : "Standard user";

    public TweaksViewModel Tweaks { get; }

    // Глобальные хоткеи живут в Gaming (быстрые действия из игры).
    public HotkeysViewModel Hotkeys { get; } = new();

    public Avalonia.Controls.Window? OwnerWindow { get; set; }

    public IRelayCommand EnableGameModeCommand { get; }
    public IRelayCommand DisableGameModeCommand { get; }
    public IRelayCommand AddProfileCommand { get; }
    public IRelayCommand DeleteProfileCommand { get; }
    public IRelayCommand SaveProfileCommand { get; }
    public IRelayCommand RenameProfileCommand { get; }
    public IRelayCommand AddProcessToKeepCommand { get; }
    public IRelayCommand AddProcessToKillCommand { get; }
    public IAsyncRelayCommand BrowseGameExeCommand { get; }
    public IRelayCommand RefreshPowerPlansCommand { get; }
    public IRelayCommand<GameProfileSnapshot> SelectProfileCommand { get; }
    public IRelayCommand<PowerPlanInfo> ApplyPowerPlanCommand { get; }
    public IRelayCommand<string> RemoveProcessToKeepCommand { get; }
    public IRelayCommand<string> RemoveProcessToKillCommand { get; }
    public IAsyncRelayCommand ToggleGameModeCommand { get; }
    public IAsyncRelayCommand BoostNowCommand { get; }
    public IAsyncRelayCommand CloseBrowsersCommand { get; }
    public IAsyncRelayCommand LaunchGameCommand { get; }
    public IAsyncRelayCommand ScanLibraryCommand { get; }
    public IRelayCommand<GameEntry> ImportGameCommand { get; }
    public IRelayCommand<string> SelectSectionCommand { get; }
    public IRelayCommand RestartAsAdminCommand { get; }

    public GameModeViewModel(GameModeService gameModeService, PowerService powerService)
    {
        _gameModeService = gameModeService;
        _powerService = powerService;
        _fileDialog = new FileDialogService();
        _elevation = new ElevationService();
        _storage = new GameProfileStorageService();
        Tweaks = new TweaksViewModel(new TweaksService());

        IsElevated = _elevation.IsElevated;

        EnableGameModeCommand = new AsyncRelayCommand(EnableGameMode);
        DisableGameModeCommand = new AsyncRelayCommand(DisableGameMode);
        AddProfileCommand = new RelayCommand(AddProfile);
        DeleteProfileCommand = new RelayCommand(DeleteProfile);
        SaveProfileCommand = new RelayCommand(SaveProfiles);
        RenameProfileCommand = new RelayCommand(RenameProfile);
        AddProcessToKeepCommand = new RelayCommand(AddProcessToKeep);
        AddProcessToKillCommand = new RelayCommand(AddProcessToKill);
        RemoveProcessToKeepCommand = new RelayCommand<string>(RemoveProcessToKeep);
        RemoveProcessToKillCommand = new RelayCommand<string>(RemoveProcessToKill);
        BrowseGameExeCommand = new AsyncRelayCommand(BrowseGameExeAsync);
        RefreshPowerPlansCommand = new RelayCommand(LoadPowerPlans);
        SelectProfileCommand = new RelayCommand<GameProfileSnapshot>(SelectProfile);
        ApplyPowerPlanCommand = new RelayCommand<PowerPlanInfo>(ApplyPowerPlan);
        ToggleGameModeCommand = new AsyncRelayCommand(ToggleGameMode);
        BoostNowCommand = new AsyncRelayCommand(BoostNow);
        CloseBrowsersCommand = new AsyncRelayCommand(CloseBrowsersAsync);
        LaunchGameCommand = new AsyncRelayCommand(LaunchGameAsync);
        ScanLibraryCommand = new AsyncRelayCommand(ScanLibraryAsync);
        ImportGameCommand = new RelayCommand<GameEntry>(ImportGame);
        SelectSectionCommand = new RelayCommand<string>(SelectSection);
        RestartAsAdminCommand = new RelayCommand(() => _elevation.RestartAsAdministrator());

        _gameModeService.OnAutoRestored += HandleAutoRestored;
        _autoDetect.OnGameDetected += HandleGameDetected;
        _autoDetect.OnAllGamesExited += HandleAllGamesExited;

        _elapsedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _elapsedTimer.Tick += (_, __) => UpdateElapsedText();
        _liveMonitor.OnHardwareUpdated += info => Dispatcher.UIThread.Post(() =>
        {
            CpuNow = Math.Round(info.CpuLoad, 0);
            // RAM — ТОЛЬКО из GlobalMemoryStatusEx (как в Dashboard):
            // сумма сенсоров LHM на части машин даёт нули/чужие total,
            // и полоска RAM в Live load вечно показывала 0%.
            try
            {
                var (total, avail) = DetailedSystemInfoService.GetPhysicalMemory();
                var used = Math.Max(0, total - avail);
                RamNow = total > 0 ? Math.Round(used / total * 100, 0) : 0;
            }
            catch { RamNow = 0; }
        });

        LoadProfiles();
        LoadPowerPlans();
    }

    public override void OnActivated()
    {
        base.OnActivated();
        _liveMonitor.Start(1500);
        RestartAutoDetect();
        Hotkeys.OnActivated();
    }

    public override void OnDeactivated()
    {
        base.OnDeactivated();
        _liveMonitor.Stop();
        _autoDetect.Stop();
    }

    partial void OnAutoDetectEnabledChanged(bool value)
    {
        if (value) RestartAutoDetect();
        else { _autoDetect.Stop(); StatusText = "Auto-detect off"; }
    }

    private void RestartAutoDetect()
    {
        if (!AutoDetectEnabled) return;
        _autoDetect.Start(() => Profiles
            .Select(p => p.GameExecutablePath)
            .Concat(DetectedGames.Select(g => g.ExePath)));
    }

    // Игра запущена извне (Steam/лаунчер): автовключаем подходящий профиль.
    private void HandleGameDetected(string exeName)
    {
        Dispatcher.UIThread.Post(async () =>
        {
            if (!AutoDetectEnabled || IsGameModeActive) return;
            var match = Profiles.FirstOrDefault(p =>
                !string.IsNullOrWhiteSpace(p.GameExecutablePath) &&
                System.IO.Path.GetFileNameWithoutExtension(p.GameExecutablePath)
                    .Equals(exeName, StringComparison.OrdinalIgnoreCase));
            if (match == null) return;
            SelectedProfile = match;
            RebuildProfileRows();
            _autoEnabled = true;
            await EnableGameMode();
            StatusText = $"Auto-enabled for {exeName} ({match.Name})";
        });
    }

    private void HandleAllGamesExited()
    {
        Dispatcher.UIThread.Post(async () =>
        {
            // Откатываем только автовключённый режим с AutoRestore
            if (!_autoEnabled || !IsGameModeActive) return;
            if (SelectedProfile?.AutoRestoreOnGameExit == true)
            {
                await DisableGameMode();
                StatusText = "Game exited — system restored";
            }
            _autoEnabled = false;
        });
    }

    private void SelectSection(string? section)
    {
        if (string.IsNullOrEmpty(section)) return;
        ActiveSection = section;
        OnPropertyChanged(nameof(IsProfilesSection));
        OnPropertyChanged(nameof(IsTweaksSection));
    }

    private void LoadProfiles()
    {
        var saved = _storage.Load();
        Profiles = new ObservableCollection<GameProfileSnapshot>(
            saved.Count > 0 ? saved : _gameModeService.GetDefaultProfiles());
        SelectedProfile = Profiles.FirstOrDefault();
        RebuildProfileRows();
    }

    private void SaveProfiles()
    {
        _storage.Save(Profiles);
        StatusText = $"Saved {Profiles.Count} profile(s) to disk";
    }

    private void SelectProfile(GameProfileSnapshot? profile)
    {
        if (profile == null) return;
        SelectedProfile = profile;
        RebuildProfileRows();
    }

    // ProfileRowViewModel хранит своё собственное IsSelected, поэтому после
    // любого изменения набора профилей или текущего выбора список нужно
    // пересобрать — иначе подсветка кнопки отстанет от реального состояния.
    private void RebuildProfileRows()
    {
        ProfileRows = new ObservableCollection<ProfileRowViewModel>(
            Profiles.Select(p => new ProfileRowViewModel(p, ReferenceEquals(p, SelectedProfile) || p == SelectedProfile)));
        SelectedRow = ProfileRows.FirstOrDefault(r =>
            ReferenceEquals(r.Profile, SelectedProfile) || r.Profile == SelectedProfile);
    }

    partial void OnSelectedRowChanged(ProfileRowViewModel? value)
    {
        if (value != null && value.Profile != SelectedProfile)
            SelectProfile(value.Profile);
    }

    private void AddProfile()
    {
        var baseName = "New Profile";
        var name = baseName;
        int suffix = 2;
        while (Profiles.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            name = $"{baseName} {suffix++}";

        var profile = _gameModeService.CreateCustomProfile(name, "");
        Profiles.Add(profile);
        SelectedProfile = profile;
        SaveProfiles();
        RebuildProfileRows();
    }

    private void DeleteProfile()
    {
        if (SelectedProfile == null || Profiles.Count <= 1) return;

        if (!IsDeleteConfirming)
        {
            IsDeleteConfirming = true;
            StatusText = "Tap Delete again to confirm.";
            _ = ResetDeleteConfirmAsync();
            return;
        }
        IsDeleteConfirming = false;

        Profiles.Remove(SelectedProfile);
        SelectedProfile = Profiles.FirstOrDefault();
        SaveProfiles();
        RebuildProfileRows();
    }

    private async Task ResetDeleteConfirmAsync()
    {
        await Task.Delay(4000);
        IsDeleteConfirming = false;
    }

    private void RenameProfile()
    {
        if (SelectedProfile == null || string.IsNullOrWhiteSpace(RenameText)) return;

        var newName = RenameText.Trim();
        if (Profiles.Any(p => p != SelectedProfile && p.Name.Equals(newName, StringComparison.OrdinalIgnoreCase)))
        {
            StatusText = "A profile with that name already exists.";
            return;
        }

        UpdateSelectedProfile(SelectedProfile with { Name = newName });
        SaveProfiles();
        RenameText = "";
        StatusText = $"Renamed to \"{newName}\"";
    }

    private async Task BrowseGameExeAsync()
    {
        if (SelectedProfile == null)
        {
            StatusText = "Select or create a profile first.";
            return;
        }

        var path = await _fileDialog.PickExecutableAsync();
        if (string.IsNullOrEmpty(path))
        {
            StatusText = "No file selected.";
            return;
        }

        UpdateSelectedProfile(SelectedProfile with { GameExecutablePath = path });
        SaveProfiles();
        StatusText = $"Selected: {System.IO.Path.GetFileName(path)}";
    }

    private async Task ToggleGameMode()
    {
        if (IsGameModeActive) await DisableGameMode();
        else await EnableGameMode();
    }

    private async Task BoostNow()
    {
        StatusText = "Boosting… trimming memory, flushing DNS";
        StatusText = await _gameModeService.BoostNowAsync();
        GameReport = _gameModeService.LastReport;
    }

    private async Task CloseBrowsersAsync()
    {
        StatusText = "Closing browsers…";
        StatusText = await _gameModeService.QuickKillBrowsersAsync();
        GameReport = _gameModeService.LastReport;
    }

    private async Task LaunchGameAsync()
    {
        if (SelectedProfile == null) { StatusText = "Select a profile first"; return; }
        if (string.IsNullOrWhiteSpace(SelectedProfile.GameExecutablePath))
        { StatusText = "No game exe in profile — Browse or import from Library"; return; }
        await EnableGameMode();
    }

    private async Task ScanLibraryAsync()
    {
        if (IsScanningLibrary) return;
        IsScanningLibrary = true;
        StatusText = "Scanning Steam / Epic / Start Menu…";
        var games = await Task.Run(GameLibraryService.ScanAll);
        DetectedGames = new ObservableCollection<GameEntry>(games);
        OnPropertyChanged(nameof(HasDetectedGames));
        StatusText = games.Count > 0 ? $"Found {games.Count} games" : "No games found";
        IsScanningLibrary = false;
    }

    private void ImportGame(GameEntry? game)
    {
        if (game == null) return;
        var baseName = game.Name;
        var name = baseName;
        int suffix = 2;
        while (Profiles.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            name = $"{baseName} {suffix++}";
        var profile = _gameModeService.CreateCustomProfile(name, game.ExePath);
        Profiles.Add(profile);
        SelectedProfile = profile;
        SaveProfiles();
        StatusText = $"Imported {game.Name} ({game.Source})";
    }

    private async Task EnableGameMode()
    {
        if (SelectedProfile == null) { StatusText = "Select a profile first"; return; }

        StatusText = "Enabling Game Mode...";
        ToggleButtonText = "ENABLING...";

        await _gameModeService.EnableAsync(SelectedProfile);

        IsGameModeActive = true;
        IsGameRunning = _gameModeService.IsGameProcessRunning;
        ToggleButtonText = "DISABLE GAME MODE";
        StatusText = IsGameRunning
            ? $"Game Mode active — {SelectedProfile.Name} launched"
            : $"Game Mode active: {SelectedProfile.Name}";
        GameReport = _gameModeService.LastReport;

        _enabledAt = DateTime.Now;
        UpdateElapsedText();
        _elapsedTimer.Start();
    }

    private async Task DisableGameMode()
    {
        StatusText = "Restoring system...";
        ToggleButtonText = "RESTORING...";

        await _gameModeService.DisableAsync();

        IsGameModeActive = false;
        IsGameRunning = false;
        ToggleButtonText = "ENABLE GAME MODE";
        StatusText = "System restored";
        GameReport = _gameModeService.LastReport;

        _elapsedTimer.Stop();
        _enabledAt = null;
        ElapsedText = "";
    }

    private void HandleAutoRestored()
    {
        Dispatcher.UIThread.Post(() =>
        {
            IsGameModeActive = false;
            IsGameRunning = false;
            ToggleButtonText = "ENABLE GAME MODE";
            StatusText = "Game exited — system automatically restored";
            _elapsedTimer.Stop();
            _enabledAt = null;
            ElapsedText = "";
        });
    }

    private void UpdateElapsedText()
    {
        if (_enabledAt == null) { ElapsedText = ""; return; }
        var span = DateTime.Now - _enabledAt.Value;
        ElapsedText = span.TotalHours >= 1
            ? $"{(int)span.TotalHours}h {span.Minutes}m"
            : $"{span.Minutes}m {span.Seconds}s";
        IsGameRunning = _gameModeService.IsGameProcessRunning;
    }

    private void AddProcessToKeep()
    {
        if (string.IsNullOrWhiteSpace(NewProcessToKeep) || SelectedProfile == null) return;
        var name = NormExeName(NewProcessToKeep);
        if (string.IsNullOrEmpty(name)) return;

        List<string> current = new(SelectedProfile.ProcessesToKeep);
        if (!current.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            current.Add(name);
            UpdateSelectedProfile(SelectedProfile with { ProcessesToKeep = current });
            SaveProfiles();
        }
        NewProcessToKeep = "";
    }

    private void AddProcessToKill()
    {
        if (string.IsNullOrWhiteSpace(NewProcessToKill) || SelectedProfile == null) return;
        var name = NormExeName(NewProcessToKill);
        if (string.IsNullOrEmpty(name)) return;

        List<string> current = new(SelectedProfile.ProcessesToKill);
        if (!current.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            current.Add(name);
            UpdateSelectedProfile(SelectedProfile with { ProcessesToKill = current });
            SaveProfiles();
        }
        NewProcessToKill = "";
    }

    private static string NormExeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var n = name.Trim().Trim('"');
        if (n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            n = n[..^4];
        return n.Trim();
    }

    private void RemoveProcessToKeep(string? process)
    {
        if (process == null || SelectedProfile == null) return;
        var current = SelectedProfile.ProcessesToKeep.Where(p => p != process).ToList();
        UpdateSelectedProfile(SelectedProfile with { ProcessesToKeep = current });
        SaveProfiles();
    }

    private void RemoveProcessToKill(string? process)
    {
        if (process == null || SelectedProfile == null) return;
        var current = SelectedProfile.ProcessesToKill.Where(p => p != process).ToList();
        UpdateSelectedProfile(SelectedProfile with { ProcessesToKill = current });
        SaveProfiles();
    }

    private void UpdateSelectedProfile(GameProfileSnapshot updated)
    {
        var idx = Profiles.IndexOf(SelectedProfile);
        if (idx >= 0)
        {
            Profiles[idx] = updated;
            SelectedProfile = updated;
            RebuildProfileRows();
        }
    }

    private void LoadPowerPlans()
    {
        PowerPlans = new ObservableCollection<PowerPlanInfo>(_powerService.GetPowerPlans());
        ActivePowerPlanName = PowerPlans.FirstOrDefault(p => p.IsActive)?.Name ?? "Balanced";
    }

    // Раньше план выбирался в списке, но применялся только отдельной кнопкой
    // "Apply", которой не было привязки к выбору — план физически нельзя
    // было сменить. Теперь клик по плану сразу его применяет.
    private void ApplyPowerPlan(PowerPlanInfo? plan)
    {
        if (plan == null) return;
        _powerService.SetActivePowerPlan(plan.Guid);
        LoadPowerPlans();
        StatusText = $"Power plan set to {plan.Name}";
    }

    partial void OnIsDeleteConfirmingChanged(bool value) => OnPropertyChanged(nameof(DeleteButtonText));
    partial void OnIsElevatedChanged(bool value) => OnPropertyChanged(nameof(ElevationLabel));
}