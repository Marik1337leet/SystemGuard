using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using SystemGuard.Desktop.Models;
using SystemGuard.Desktop.Services;

namespace SystemGuard.Desktop.ViewModels;

public partial class PerformanceViewModel : ViewModelBase
{
    private readonly PowerService _powerService = new();
    private readonly BenchmarkService _benchmarkService = new();
    private readonly BenchmarkHistoryService _benchHistory = new();
    private readonly AuditLogService _audit = new();

    [ObservableProperty] private ObservableCollection<PowerPlanInfo> _powerPlans = new();
    [ObservableProperty] private PowerPlanInfo? _selectedPlan;
    [ObservableProperty] private BatteryInfo? _batteryInfo;
    [ObservableProperty] private ObservableCollection<BenchmarkResult> _benchmarkResults = new();
    [ObservableProperty] private bool _isBenchmarking;
    [ObservableProperty] private string _statusText = "Ready";
    [ObservableProperty] private double _benchmarkProgress;
    [ObservableProperty] private string _activePowerPlanName = "Balanced";
    [ObservableProperty] private bool _hasBattery;
    [ObservableProperty] private string _batteryEstimatedTime = "N/A";
    [ObservableProperty] private string _batteryStatus = "Unknown";
    [ObservableProperty] private string _batteryChargeColor = "#22C55E";
    [ObservableProperty] private string _overallRating = "";
    [ObservableProperty] private string _systemSummary = "";

    // Текстовое поле для ввода минут перед Timer Shutdown
    [ObservableProperty] private string _shutdownDelayMinutes = "60";

    // История бенчмарков / стресс / стоимость энергии / системные инструменты
    [ObservableProperty] private ObservableCollection<BenchmarkRun> _benchmarkHistory = new();
    [ObservableProperty] private string _comparisonText = "";
    [ObservableProperty] private int _stressSeconds = 30;
    [ObservableProperty] private bool _isStressing;
    [ObservableProperty] private string _stressResult = "";
    [ObservableProperty] private double _pcWatts = 150;
    [ObservableProperty] private double _pcHoursPerDay = 6;
    [ObservableProperty] private double _kwhTariff = 6;
    [ObservableProperty] private string _powerCostText = "";
    [ObservableProperty] private string _registryPath = @"Software\SystemGuard";
    [ObservableProperty] private string _registryName = "Test";
    [ObservableProperty] private string _registryValue = "";
    [ObservableProperty] private string _registryOutput = "";
    [ObservableProperty] private ObservableCollection<EventLogEntry> _eventEntries = new();
    [ObservableProperty] private ObservableCollection<DeviceEntry> _devices = new();
    [ObservableProperty] private string _tweakStatus = "";

    [ObservableProperty] private ISeries[] _benchmarkChartSeries = Array.Empty<ISeries>();
    [ObservableProperty] private Axis[] _benchmarkXAxes = Array.Empty<Axis>();
    [ObservableProperty] private Axis[] _benchmarkYAxes = Array.Empty<Axis>();

    public IRelayCommand ActivatePlanCommand { get; }
    public IRelayCommand RunBenchmarksCommand { get; }
    public IRelayCommand ShutdownCommand { get; }
    public IRelayCommand RestartCommand { get; }
    public IRelayCommand SleepCommand { get; }
    public IRelayCommand HibernateCommand { get; }
    public IRelayCommand LockCommand { get; }
    public IRelayCommand TimedShutdownCommand { get; }
    public IRelayCommand FreeMemoryCommand { get; }
    public IRelayCommand RunStressCommand { get; }
    public IRelayCommand LoadBenchHistoryCommand { get; }
    public IRelayCommand CalcPowerCostCommand { get; }
    public IRelayCommand RegistryReadCommand { get; }
    public IRelayCommand RegistryWriteCommand { get; }
    public IRelayCommand LoadEventsCommand { get; }
    public IRelayCommand LoadDevicesCommand { get; }
    public IRelayCommand OpenDevicesCommand { get; }
    public IRelayCommand TelemetryOffCommand { get; }
    public IRelayCommand TelemetryOnCommand { get; }
    public IRelayCommand BitLockerCommand { get; }

    public PerformanceViewModel()
    {
        ActivatePlanCommand = new RelayCommand(ActivatePlan);
        RunBenchmarksCommand = new AsyncRelayCommand(RunBenchmarks);
        ShutdownCommand = new RelayCommand(() => _powerService.Shutdown());
        RestartCommand = new RelayCommand(() => _powerService.Restart());
        SleepCommand = new RelayCommand(() => _powerService.Sleep());
        HibernateCommand = new RelayCommand(() => _powerService.Hibernate());
        LockCommand = new RelayCommand(() => _powerService.LockWorkstation());
        TimedShutdownCommand = new RelayCommand(TimedShutdown);
        FreeMemoryCommand = new AsyncRelayCommand(FreeMemoryAsync);
        RunStressCommand = new AsyncRelayCommand(RunStressAsync);
        LoadBenchHistoryCommand = new RelayCommand(LoadBenchHistory);
        CalcPowerCostCommand = new RelayCommand(() => PowerCostText = PowerCostService.Format(PcWatts, PcHoursPerDay, KwhTariff));
        RegistryReadCommand = new RelayCommand(() => RegistryOutput = string.Join("\n", RegistryToolService.ListValues(RegistryPath)));
        RegistryWriteCommand = new RelayCommand(() => { RegistryOutput = RegistryToolService.SetValue(RegistryPath, RegistryName, RegistryValue); _audit.Log("System", "RegistryWrite", RegistryPath); });
        LoadEventsCommand = new RelayCommand(() => EventEntries = new ObservableCollection<EventLogEntry>(EventLogService.Read("Application", 30)));
        LoadDevicesCommand = new RelayCommand(() => Devices = new ObservableCollection<DeviceEntry>(DeviceInfoService.ListDevices()));
        OpenDevicesCommand = new RelayCommand(() => StatusText = DeviceInfoService.OpenDeviceManager());
        TelemetryOffCommand = new RelayCommand(() => { TweakStatus = WindowsTweakerService.SetTelemetry(true); _audit.Log("System", "TelemetryOff", TweakStatus); });
        TelemetryOnCommand = new RelayCommand(() => { TweakStatus = WindowsTweakerService.SetTelemetry(false); _audit.Log("System", "TelemetryOn", TweakStatus); });
        BitLockerCommand = new RelayCommand(() => TweakStatus = WindowsTweakerService.BitLockerStatus());

        InitBenchmarkChart();
        LoadData();
        LoadBenchHistory();
    }

    public override void OnActivated()
    {
        base.OnActivated();
        LoadData();
    }

    private void InitBenchmarkChart()
    {
        var transparent = new SolidColorPaint(new SKColor(255, 255, 255, 0));
        var faint = new SolidColorPaint(new SKColor(255, 255, 255, 10));

        BenchmarkXAxes = new[] { new Axis { LabelsPaint = transparent, SeparatorsPaint = transparent, TextSize = 0 } };
        BenchmarkYAxes = new[] { new Axis { MinLimit = 0, MaxLimit = 100, LabelsPaint = new SolidColorPaint(new SKColor(255, 255, 255, 60)), SeparatorsPaint = faint, TextSize = 10 } };
    }

    private void LoadData()
    {
        PowerPlans = new ObservableCollection<PowerPlanInfo>(_powerService.GetPowerPlans());
        ActivePowerPlanName = PowerPlans.FirstOrDefault(p => p.IsActive)?.Name ?? "Balanced";

        var battery = _powerService.GetBatteryInfo();
        if (battery != null)
        {
            HasBattery = true;
            BatteryInfo = battery;

            // Теперь реальные данные из PowerService (Этап Г) вместо догадок
            BatteryStatus = battery.Status;
            BatteryEstimatedTime = battery.TimeRemainingText;
            BatteryChargeColor = battery.IsCharging
                ? "#3B82F6" // синий — заряжается
                : battery.ChargePercent > 50 ? "#22C55E"
                : battery.ChargePercent > 20 ? "#F59E0B"
                : "#EF4444";
        }
        else
        {
            HasBattery = false;
        }
    }

    private void ActivatePlan()
    {
        if (SelectedPlan == null) return;
        _powerService.SetActivePowerPlan(SelectedPlan.Guid);
        LoadData();
    }

    private void TimedShutdown()
    {
        if (!int.TryParse(ShutdownDelayMinutes, out var minutes) || minutes <= 0)
        {
            StatusText = "Enter a valid number of minutes";
            return;
        }
        _powerService.Shutdown(minutes * 60);
        StatusText = $"Shutdown scheduled in {minutes} minutes";
    }

    private async Task FreeMemoryAsync()
    {
        StatusText = "Trimming working sets…";
        var n = await MemoryTrimmer.TrimAllAsync();
        StatusText = $"Memory trimmed: {n} processes";
    }

    private async Task RunBenchmarks()
    {
        IsBenchmarking = true;
        BenchmarkProgress = 0;
        BenchmarkResults.Clear();
        OverallRating = "";
        SystemSummary = "";

        var step = 100.0 / 8;

        StatusText = "CPU Single Core..."; BenchmarkProgress = step;
        BenchmarkResults.Add(await _benchmarkService.BenchmarkCpuSingleCore());

        StatusText = "CPU Multi Core..."; BenchmarkProgress = step * 2;
        BenchmarkResults.Add(await _benchmarkService.BenchmarkCpuMultiCore());

        StatusText = "Memory Bandwidth..."; BenchmarkProgress = step * 3;
        BenchmarkResults.Add(await _benchmarkService.BenchmarkMemoryBandwidth());

        StatusText = "Memory Latency..."; BenchmarkProgress = step * 4;
        BenchmarkResults.Add(await _benchmarkService.BenchmarkMemoryLatency());

        StatusText = "Disk Sequential Write..."; BenchmarkProgress = step * 5;
        BenchmarkResults.Add(await _benchmarkService.BenchmarkDiskSequentialWrite());

        StatusText = "Disk Sequential Read..."; BenchmarkProgress = step * 6;
        BenchmarkResults.Add(await _benchmarkService.BenchmarkDiskSequentialRead());

        StatusText = "Disk Random Access..."; BenchmarkProgress = step * 7;
        BenchmarkResults.Add(await _benchmarkService.BenchmarkDiskRandomAccess());

        StatusText = "Network Speed..."; BenchmarkProgress = step * 8;
        BenchmarkResults.Add(await _benchmarkService.BenchmarkNetworkSpeed());

        BenchmarkProgress = 100;
        StatusText = "Complete";

        var results = BenchmarkResults.ToList();
        OverallRating = _benchmarkService.GetOverallRating(results);
        SystemSummary = _benchmarkService.GetSystemSummary(results);

        // Цвета по рейтингу — реально применяются к каждому столбцу графика
        var values = results.Select(r => r.PercentageVsReference).ToArray();
        var series = new ISeries[results.Count];
        for (int i = 0; i < results.Count; i++)
        {
            var color = GetColorForRating(results[i].Rating);
            series[i] = new ColumnSeries<double>
            {
                Values = new[] { values[i] },
                Fill = new SolidColorPaint(new SKColor(color.Red, color.Green, color.Blue, 180)),
                Stroke = new SolidColorPaint(color, 2),
                MaxBarWidth = 50,
                Padding = 4,
                Name = results[i].TestName
            };
        }
        BenchmarkChartSeries = series;

        // История + сравнение с предыдущим прогоном
        _benchHistory.SaveRun(results);
        LoadBenchHistory();
        if (BenchmarkHistory.Count >= 2)
        {
            var cur = BenchmarkHistory[^1];
            var prev = BenchmarkHistory[^2];
            ComparisonText = string.Join("\n", BenchmarkHistoryService.CompareRuns(cur, prev));
        }
        else ComparisonText = "First run saved — run again to compare";
        _audit.Log("Benchmark", "RunAll", OverallRating);

        IsBenchmarking = false;
    }

    private void LoadBenchHistory() =>
        BenchmarkHistory = new ObservableCollection<BenchmarkRun>(_benchHistory.ListRuns().TakeLast(10).Reverse());

    private async Task RunStressAsync()
    {
        if (IsStressing) return;
        IsStressing = true;
        StressResult = "Preparing stress test…";
        try
        {
            var svc = new HardwareMonitorService();
            var progress = new Progress<string>(m => StressResult = m);
            var res = await StressTestService.RunCpuStressAsync(
                Math.Clamp(StressSeconds, 5, 600),
                () => svc.ReadCpuOnce(),
                progress);
            StressResult = res.ThrottlingDetected
                ? $"THROTTLING: {res.Details}"
                : $"OK: {res.Details}";
            _audit.Log("Benchmark", "Stress", StressResult);
            svc.Dispose();
        }
        catch (Exception ex) { StressResult = $"Stress failed: {ex.Message}"; }
        finally { IsStressing = false; }
    }

    private static SKColor GetColorForRating(string rating) => rating switch
    {
        "Outstanding" => new SKColor(34, 197, 94),
        "Excellent" => new SKColor(59, 130, 246),
        "Good" => new SKColor(168, 85, 247),
        "Average" => new SKColor(245, 158, 11),
        "Below Average" => new SKColor(249, 115, 22),
        "Poor" => new SKColor(239, 68, 68),
        _ => new SKColor(100, 100, 100)
    };
}
