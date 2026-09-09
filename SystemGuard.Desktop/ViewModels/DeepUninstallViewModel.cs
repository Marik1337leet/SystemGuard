using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SystemGuard.Desktop.Services;

namespace SystemGuard.Desktop.ViewModels;

public partial class DeepUninstallViewModel : ViewModelBase
{
    private readonly DeepUninstallService _service = new();
    private CancellationTokenSource? _cts;

    // ── Программы ─────────────────────────────────────────────────────────────
    [ObservableProperty] private ObservableCollection<InstalledProgram> _programs = new();
    [ObservableProperty] private ObservableCollection<InstalledProgram> _filteredPrograms = new();
    [ObservableProperty] private InstalledProgram? _selectedProgram;
    [ObservableProperty] private string _searchText = "";

    // ── Следы ─────────────────────────────────────────────────────────────────
    [ObservableProperty] private ObservableCollection<UninstallTrace> _traces = new();
    [ObservableProperty] private bool _hasTraces;

    // ── Статус ────────────────────────────────────────────────────────────────
    [ObservableProperty] private string _statusText = "Load the program list to start";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private bool _isUninstalling;
    [ObservableProperty] private bool _showUninstallResult;
    [ObservableProperty] private string _uninstallResultText = "";
    [ObservableProperty] private string _uninstallResultColor = "#34D399";

    // ── Выбор следов ──────────────────────────────────────────────────────────
    public long TotalSelectedSize => Traces.Where(t => t.IsSelected).Sum(t => t.SizeBytes);
    public int TotalSelectedCount => Traces.Count(t => t.IsSelected);

    // ── Команды ───────────────────────────────────────────────────────────────
    public IRelayCommand LoadProgramsCommand { get; }
    public IRelayCommand ScanTracesCommand { get; }
    public IRelayCommand RunStandardUninstallerCommand { get; }
    public IRelayCommand DeepRemoveCommand { get; }
    public IRelayCommand SelectAllTracesCommand { get; }
    public IRelayCommand DeselectAllTracesCommand { get; }
    public IRelayCommand OpenLocationCommand { get; }
    public IRelayCommand CancelCommand { get; }

    public DeepUninstallViewModel()
    {
        LoadProgramsCommand = new AsyncRelayCommand(LoadProgramsAsync);
        ScanTracesCommand = new AsyncRelayCommand(ScanTracesAsync, () => SelectedProgram != null);
        RunStandardUninstallerCommand = new AsyncRelayCommand(RunStandardAsync, () => SelectedProgram != null);
        DeepRemoveCommand = new AsyncRelayCommand(DeepRemoveAsync, () => HasTraces && Traces.Any(t => t.IsSelected));
        SelectAllTracesCommand = new RelayCommand(() => SetAllSelected(true));
        DeselectAllTracesCommand = new RelayCommand(() => SetAllSelected(false));
        OpenLocationCommand = new RelayCommand(OpenLocation);
        CancelCommand = new RelayCommand(Cancel);
    }

    public override void OnActivated()
    {
        base.OnActivated();
        if (Programs.Count == 0)
            _ = LoadProgramsAsync();
    }

    // ── Load programs ─────────────────────────────────────────────────────────

    private async Task LoadProgramsAsync()
    {
        IsScanning = true;
        StatusText = "Reading installed programs...";
        Progress = 0;

        var list = await _service.GetInstalledProgramsAsync();

        Programs = new ObservableCollection<InstalledProgram>(list);
        ApplySearch();

        Progress = 100;
        StatusText = $"{list.Count} programs found";
        IsScanning = false;
    }

    partial void OnSearchTextChanged(string value) => ApplySearch();

    private void ApplySearch()
    {
        if (string.IsNullOrWhiteSpace(SearchText))
        {
            FilteredPrograms = new ObservableCollection<InstalledProgram>(Programs);
        }
        else
        {
            var s = SearchText.ToLower();
            FilteredPrograms = new ObservableCollection<InstalledProgram>(
                Programs.Where(p =>
                    p.Name.Contains(s, StringComparison.OrdinalIgnoreCase) ||
                    p.Publisher.Contains(s, StringComparison.OrdinalIgnoreCase)));
        }
    }

    partial void OnSelectedProgramChanged(InstalledProgram? value)
    {
        Traces.Clear();
        HasTraces = false;
        ShowUninstallResult = false;
        (ScanTracesCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();
        (RunStandardUninstallerCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();
    }

    // ── Scan traces ───────────────────────────────────────────────────────────

    private async Task ScanTracesAsync()
    {
        if (SelectedProgram == null) return;

        _cts = new CancellationTokenSource();
        IsScanning = true;
        Traces.Clear();
        HasTraces = false;
        ShowUninstallResult = false;
        Progress = 0;
        StatusText = $"Scanning traces of {SelectedProgram.Name}...";

        var progressReporter = new Progress<string>(msg =>
        {
            StatusText = msg;
            Progress = Math.Min(Progress + 15, 90);
        });

        try
        {
            var traces = await _service.FindTracesAsync(
                SelectedProgram, progressReporter, _cts.Token);

            foreach (var t in traces)
                Traces.Add(t);

            SubscribeTraces();
            HasTraces = Traces.Count > 0;
            Progress = 100;
            StatusText = HasTraces
                ? $"Found {Traces.Count} traces — review and click Deep Remove"
                : "No additional traces found";

            NotifySelectionChanged();
            (DeepRemoveCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();
        }
        catch (OperationCanceledException)
        {
            StatusText = "Scan cancelled";
        }
        finally
        {
            IsScanning = false;
        }
    }

    // ── Standard uninstaller ──────────────────────────────────────────────────

    private async Task RunStandardAsync()
    {
        if (SelectedProgram == null) return;

        IsUninstalling = true;
        StatusText = $"Running uninstaller for {SelectedProgram.Name}...";

        var ok = await _service.RunStandardUninstallerAsync(SelectedProgram);

        IsUninstalling = false;
        if (ok)
        {
            StatusText = "Standard uninstaller finished. Now scan for remaining traces.";
            // Автоматически запускаем сканирование следов
            await ScanTracesAsync();
        }
        else
        {
            StatusText = "Could not start standard uninstaller (no uninstall string found)";
        }
    }

    // ── Deep remove ───────────────────────────────────────────────────────────

    private async Task DeepRemoveAsync()
    {
        if (!HasTraces) return;

        _cts = new CancellationTokenSource();
        IsUninstalling = true;
        Progress = 0;
        ShowUninstallResult = false;

        var selected = Traces.Where(t => t.IsSelected).ToList();
        StatusText = $"Removing {selected.Count} traces...";

        var progressReporter = new Progress<string>(msg =>
        {
            StatusText = msg;
            Progress = Math.Min(Progress + 100.0 / selected.Count, 95);
        });

        try
        {
            var result = await _service.RemoveTracesAsync(
                Traces.ToList(), progressReporter, _cts.Token);

            Progress = 100;

            var freed = FormatBytes(result.FreedBytes);
            UninstallResultText = result.Errors.Count == 0
                ? $"Removed {result.RemovedCount} items, freed {freed}"
                : $"Removed {result.RemovedCount} items, freed {freed}\n" +
                  $"{result.Errors.Count} errors:\n" +
                  string.Join("\n", result.Errors.Take(5));

            UninstallResultColor = result.Errors.Count == 0 ? "#34D399" : "#F59E0B";
            ShowUninstallResult = true;
            StatusText = $"Deep removal complete. {result.RemovedCount} items removed.";

            // Убираем удалённые следы из списка
            foreach (var path in result.Removed)
            {
                var trace = Traces.FirstOrDefault(t =>
                    t.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
                if (trace != null) Traces.Remove(trace);
            }

            HasTraces = Traces.Count > 0;
            (DeepRemoveCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();

            // Обновляем список программ
            await LoadProgramsAsync();
        }
        catch (OperationCanceledException)
        {
            StatusText = "Removal cancelled";
        }
        finally
        {
            IsUninstalling = false;
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void SetAllSelected(bool value)
    {
        foreach (var t in Traces) t.IsSelected = value;
        NotifySelectionChanged();
    }

    private void SubscribeTraces()
    {
        foreach (var t in Traces)
            t.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(UninstallTrace.IsSelected))
                    NotifySelectionChanged();
            };
    }

    private void NotifySelectionChanged()
    {
        OnPropertyChanged(nameof(TotalSelectedSize));
        OnPropertyChanged(nameof(TotalSelectedCount));
        (DeepRemoveCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();
    }

    private void Cancel() => _cts?.Cancel();

    private void OpenLocation()
    {
        if (SelectedProgram == null) return;
        try
        {
            var dir = SelectedProgram.InstallLocation;
            if (string.IsNullOrWhiteSpace(dir) || !System.IO.Directory.Exists(dir))
            {
                StatusText = "Install location unknown";
                return;
            }
            if (SelfProtection.IsProtectedPath(dir)) { StatusText = "Protected path"; return; }
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", dir)
            { UseShellExecute = true });
        }
        catch (Exception ex) { StatusText = $"Cannot open: {ex.Message}"; }
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1_073_741_824 => $"{bytes / 1_073_741_824.0:F2} GB",
        >= 1_048_576 => $"{bytes / 1_048_576.0:F1} MB",
        >= 1024 => $"{bytes / 1024.0:F0} KB",
        _ => $"{bytes} B"
    };
}
