using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using SystemGuard.Desktop.Services;

namespace SystemGuard.Desktop.ViewModels;

// ════════════════════════════════════════════════════════════════════════════
// ФАЙЛ: ViewModels/StartupViewModel.cs
//
// Если ты видишь ошибки типа "The property 'SelectedStartup' does not exist
// on StartupViewModel" — это значит компилятор использует СТАРУЮ версию
// этого файла, а не эту. Проверь:
//   1. Файл лежит ровно по пути ViewModels/StartupViewModel.cs
//   2. В проекте НЕТ второго файла с классом StartupViewModel
//      (например ViewModels/StartupViewModel_FIXED.cs или _FINAL.cs —
//      если ты его тоже добавил в проект, удали, оставь только этот)
//   3. Solution Explorer показывает только один StartupViewModel.cs
//   4. Сделай Clean + Rebuild (не просто Build) — Avalonia XAML-компилятор
//      кэширует разрешённые типы и может не подхватить замену с первого раза
// ════════════════════════════════════════════════════════════════════════════
public partial class StartupViewModel : ViewModelBase
{
    private readonly StartupService _startupService = new();
    private readonly WindowsServiceManager _serviceManager = new();

    [ObservableProperty] private ObservableCollection<StartupItem> _startupItems = new();
    [ObservableProperty] private ObservableCollection<WindowsServiceInfo> _services = new();
    [ObservableProperty] private StartupItem? _selectedStartup;
    [ObservableProperty] private WindowsServiceInfo? _selectedService;
    [ObservableProperty] private string _statusText = "Ready";
    [ObservableProperty] private string _startupImpact = "";
    [ObservableProperty] private string _selectedTab = "Startup";

    public bool IsStartupTab => SelectedTab == "Startup";
    public bool IsServicesTab => SelectedTab == "Services";

    // Tab selection
    public IRelayCommand<string> SelectTabCommand { get; }
    public IRelayCommand RefreshCommand { get; }
    public IRelayCommand OpenStartupFolderCommand { get; }
    public IRelayCommand OpenTaskManagerCommand { get; }

    // Toolbar-level commands — работают через SelectedStartup/SelectedService
    public IRelayCommand DisableStartupCommand { get; }
    public IRelayCommand StartServiceCommand { get; }
    public IRelayCommand StopServiceCommand { get; }

    // Inline row-level commands — принимают параметр напрямую из DataTemplate
    public IRelayCommand<StartupItem> DisableStartupItemCommand { get; }
    public IRelayCommand<string> StartServiceByNameCommand { get; }
    public IRelayCommand<string> StopServiceByNameCommand { get; }

    public StartupViewModel()
    {
        SelectTabCommand = new RelayCommand<string>(SelectTab);
        RefreshCommand = new AsyncRelayCommand(LoadAllAsync);
        OpenStartupFolderCommand = new RelayCommand(() =>
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                    "explorer.exe", Environment.GetFolderPath(Environment.SpecialFolder.Startup))
                { UseShellExecute = true });
            }
            catch { }
        });
        OpenTaskManagerCommand = new RelayCommand(() =>
        {
            try { System.Diagnostics.Process.Start("taskmgr.exe"); } catch { }
        });

        DisableStartupCommand = new RelayCommand(
            () => DisableStartup(SelectedStartup),
            () => SelectedStartup != null);

        StartServiceCommand = new RelayCommand(
            () => StartService(SelectedService?.Name),
            () => SelectedService != null && !SelectedService.IsRunning);

        StopServiceCommand = new RelayCommand(
            () => StopService(SelectedService?.Name),
            () => SelectedService != null && SelectedService.IsRunning);

        DisableStartupItemCommand = new RelayCommand<StartupItem>(DisableStartup);
        StartServiceByNameCommand = new RelayCommand<string>(StartService);
        StopServiceByNameCommand = new RelayCommand<string>(StopService);
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    public override void OnActivated()
    {
        base.OnActivated();
        _ = LoadAllAsync();
    }

    // ── Tab ───────────────────────────────────────────────────────────────────

    private void SelectTab(string? tab)
    {
        if (string.IsNullOrEmpty(tab)) return;
        SelectedTab = tab;
        OnPropertyChanged(nameof(IsStartupTab));
        OnPropertyChanged(nameof(IsServicesTab));
    }

    partial void OnSelectedTabChanged(string value)
    {
        OnPropertyChanged(nameof(IsStartupTab));
        OnPropertyChanged(nameof(IsServicesTab));
    }

    partial void OnSelectedStartupChanged(StartupItem? value)
        => (DisableStartupCommand as RelayCommand)?.NotifyCanExecuteChanged();

    partial void OnSelectedServiceChanged(WindowsServiceInfo? value)
    {
        (StartServiceCommand as RelayCommand)?.NotifyCanExecuteChanged();
        (StopServiceCommand as RelayCommand)?.NotifyCanExecuteChanged();
    }

    // ── Load ──────────────────────────────────────────────────────────────────

    private async Task LoadAllAsync()
    {
        StatusText = "Loading...";

        var startupItems = await Task.Run(() => _startupService.GetStartupItems());
        var services = await Task.Run(() => _serviceManager.GetServices());
        var impact = await Task.Run(() => _startupService.GetStartupImpact());

        StartupItems = new ObservableCollection<StartupItem>(startupItems);
        Services = new ObservableCollection<WindowsServiceInfo>(services);
        StartupImpact = FormatBytes(impact);
        StatusText = $"{startupItems.Count} startup items, {services.Count} services";
    }

    // ── Startup ───────────────────────────────────────────────────────────────

    private void DisableStartup(StartupItem? item)
    {
        if (item == null) return;
        _startupService.DisableStartupItem(item);
        _ = LoadAllAsync();
        StatusText = $"Disabled: {item.Name}";
    }

    // ── Services ──────────────────────────────────────────────────────────────

    private void StartService(string? name)
    {
        if (string.IsNullOrEmpty(name)) return;
        _serviceManager.StartService(name);
        _ = LoadAllAsync();
        StatusText = $"Started: {name}";
    }

    private void StopService(string? name)
    {
        if (string.IsNullOrEmpty(name)) return;
        _serviceManager.StopService(name);
        _ = LoadAllAsync();
        StatusText = $"Stopped: {name}";
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1_073_741_824 => $"{bytes / 1_073_741_824.0:F1} GB",
        >= 1_048_576 => $"{bytes / 1_048_576.0:F1} MB",
        _ => $"{bytes / 1024.0:F0} KB"
    };
}