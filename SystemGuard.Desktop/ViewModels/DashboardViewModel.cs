using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveChartsCore;
using LiveChartsCore.Defaults;
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

public partial class DashboardViewModel : ViewModelBase
{
    private readonly HardwareMonitorService _monitor;
    private readonly MonitoringHistoryService _history = new();
    private readonly AlertService _alerts = new();

    private readonly ObservableCollection<ObservablePoint> _cpuPoints = new();
    private readonly ObservableCollection<ObservablePoint> _gpuPoints = new();
    private readonly ObservableCollection<ObservablePoint> _ramPoints = new();
    private readonly ObservableCollection<ObservablePoint> _netPoints = new();
    private double _tick;
    private const int MaxPoints = 40;

    [ObservableProperty] private string _cpuName = "Detecting...";
    [ObservableProperty] private double _cpuLoad;
    [ObservableProperty] private double _cpuTemperature;
    [ObservableProperty] private double _cpuClock;
    [ObservableProperty] private double _cpuPower;

    [ObservableProperty] private string _gpuName = "Detecting...";
    [ObservableProperty] private double _gpuLoad;
    [ObservableProperty] private double _gpuTemperature;
    [ObservableProperty] private double _gpuMemoryUsedGb;
    [ObservableProperty] private double _gpuMemoryTotalGb;
    [ObservableProperty] private string _gpuMemoryText = "-- / -- GB";

    [ObservableProperty] private double _memoryUsedGb;
    [ObservableProperty] private double _memoryTotalGb;
    [ObservableProperty] private double _memoryLoadPercent;

    [ObservableProperty] private double _networkDownloadMbps;
    [ObservableProperty] private double _networkUploadMbps;

    // Системная полоса: дешёвые данные, обновляем раз в ~5 тиков
    [ObservableProperty] private string _uptimeText = "—";
    [ObservableProperty] private int _processCount;
    [ObservableProperty] private string _osName = "";

    [ObservableProperty] private ObservableCollection<StorageDriveViewModel> _drives = new();

    [ObservableProperty] private ISeries[] _cpuSeries = Array.Empty<ISeries>();
    [ObservableProperty] private ISeries[] _gpuSeries = Array.Empty<ISeries>();
    [ObservableProperty] private ISeries[] _ramSeries = Array.Empty<ISeries>();
    [ObservableProperty] private ISeries[] _networkSeries = Array.Empty<ISeries>();
    [ObservableProperty] private Axis[] _xAxes = Array.Empty<Axis>();
    [ObservableProperty] private Axis[] _yAxes = Array.Empty<Axis>();
    [ObservableProperty] private Axis[] _networkYAxes = Array.Empty<Axis>();

    public bool HasDrives => Drives.Count > 0;

    // История / алерты / отчёты
    [ObservableProperty] private string _historyRange = "24h";
    [ObservableProperty] private bool _is24h = true;
    [ObservableProperty] private bool _is7d;
    [ObservableProperty] private bool _is30d;
    [ObservableProperty] private ObservableCollection<SystemAlert> _alertsList = new();
    [ObservableProperty] private string _statusText = "Ready";

    // Продвинутая аналитика: прогноз, аномалии, детали железа (обновляются фоном)
    [ObservableProperty] private string _forecastText = "Forecast: collecting data…";
    [ObservableProperty] private string _anomalyText = "Anomalies: —";
    [ObservableProperty] private string _systemDetailsText = "Details: collecting…";
    private string _cachedDetails = "";
    private int _detailsTick;

    public IRelayCommand<string> SelectHistoryRangeCommand { get; }
    public IRelayCommand ExportCsvCommand { get; }
    public IRelayCommand ExportJsonCommand { get; }
    public IRelayCommand FreeMemoryCommand { get; }
    public IRelayCommand FlushDnsCommand { get; }
    public IRelayCommand CopySummaryCommand { get; }

    public DashboardViewModel()
    {
        _monitor = new HardwareMonitorService();
        _monitor.OnHardwareUpdated += OnHardwareUpdated;
        OsName = $"{Environment.OSVersion.VersionString}";
        InitCharts();
        SelectHistoryRangeCommand = new RelayCommand<string>(SelectHistoryRange);
        ExportCsvCommand = new RelayCommand(() => ExportReport("csv"));
        ExportJsonCommand = new RelayCommand(() => ExportReport("json"));
        FreeMemoryCommand = new AsyncRelayCommand(FreeMemoryAsync);
        FlushDnsCommand = new RelayCommand(() => { try { System.Diagnostics.Process.Start("ipconfig", "/flushdns"); StatusText = "DNS cache flushed"; } catch (Exception ex) { StatusText = ex.Message; } });
        CopySummaryCommand = new RelayCommand(CopySummary);
        foreach (var a in _alerts.History.TakeLast(5))
            AlertsList.Add(a);
        _alerts.OnAlert += a => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            AlertsList.Insert(0, a);
            while (AlertsList.Count > 5) AlertsList.RemoveAt(AlertsList.Count - 1);
        });
    }

    public override void OnActivated()
    {
        base.OnActivated();
        _monitor.Start(1000);
    }

    public override void OnDeactivated()
    {
        base.OnDeactivated();
        _monitor.Stop();
    }

    private void InitCharts()
    {
        static LinearGradientPaint Grad(SKColor c) =>
            new(new SKColor(c.Red, c.Green, c.Blue, 60),
                new SKColor(c.Red, c.Green, c.Blue, 5),
                new SKPoint(0.5f, 0), new SKPoint(0.5f, 1));

        var cpu = new SKColor(201, 108, 158);
        var gpu = new SKColor(212, 140, 181);
        var ram = new SKColor(201, 108, 158);
        var net = new SKColor(212, 140, 181);

        CpuSeries = new[] { new LineSeries<ObservablePoint> { Values = _cpuPoints, Stroke = new SolidColorPaint(cpu, 2), Fill = Grad(cpu), GeometrySize = 0, LineSmoothness = 0.65 } };
        GpuSeries = new[] { new LineSeries<ObservablePoint> { Values = _gpuPoints, Stroke = new SolidColorPaint(gpu, 2), Fill = Grad(gpu), GeometrySize = 0, LineSmoothness = 0.65 } };
        RamSeries = new[] { new LineSeries<ObservablePoint> { Values = _ramPoints, Stroke = new SolidColorPaint(ram, 2), Fill = Grad(ram), GeometrySize = 0, LineSmoothness = 0.65 } };
        NetworkSeries = new[] { new LineSeries<ObservablePoint> { Values = _netPoints, Stroke = new SolidColorPaint(net, 2), Fill = Grad(net), GeometrySize = 0, LineSmoothness = 0.65 } };

        var t = new SolidColorPaint(SKColors.Transparent);
        var fl = new SolidColorPaint(new SKColor(255, 255, 255, 10));

        XAxes = new[] { new Axis { LabelsPaint = t, SeparatorsPaint = t } };
        YAxes = new[] { new Axis { MinLimit = 0, MaxLimit = 100, LabelsPaint = t, SeparatorsPaint = fl } };
        NetworkYAxes = new[] { new Axis { MinLimit = 0, LabelsPaint = t, SeparatorsPaint = fl } };
    }

    private void OnHardwareUpdated(HardwareInfo info)
    {
        // Тяжёлая часть — на фоновом потоке таймера, в UI уходят только готовые цифры.
        // Раньше всё (история, алерты, WMI-планирование) выполнялось внутри UIThread.Post
        // и вешало интерфейс на каждом тике.
        _tick++;

        var cpuLoad = Math.Round(info.CpuLoad, 1);
        var cpuTemp = Math.Round(info.CpuTemperature, 1);
        var cpuClock = Math.Round(info.CpuClock / 1000.0, 2);
        var cpuPower = Math.Round(info.CpuPower, 1);
        var gpuLoad = Math.Round(info.GpuLoad, 1);
        var gpuTemp = Math.Round(info.GpuTemperature, 1);
        var gpuUsed = Math.Round(info.GpuMemoryUsed / 1024.0, 2);
        var gpuTotal = Math.Round(info.GpuMemoryTotal / 1024.0, 2);
        var memUsed = Math.Round(info.MemoryUsed, 2);
        var memTotal = Math.Round(info.MemoryUsed + info.MemoryAvailable, 2);
        var memPct = memTotal > 0 ? Math.Round(memUsed / memTotal * 100.0, 1) : 0;
        var netDown = Math.Round(info.NetworkDownload / 125_000.0, 2);
        var netUp = Math.Round(info.NetworkUpload / 125_000.0, 2);
        var tick = (int)_tick;

        // История и алерты — дешёвые, но всё равно вне UI-потока.
        // Свободное место берём из DriveInfo (реальные диски), а НЕ из сенсоров
        // LibreHardwareMonitor: там бывают дубли/не те единицы — и прилетали
        // ложные "Low disk space", хотя на диске сотни ГБ свободны.
        var diskFreeGb = AlertService.MinDiskFreeGb();
        _history.Record(new MonitoringSample
        {
            CpuLoad = cpuLoad, CpuTemp = cpuTemp, RamPercent = memPct,
            NetDownMbps = netDown, NetUpMbps = netUp,
            DiskFreeGb = diskFreeGb
        });
        if (tick % 60 == 0) _ = Task.Run(() => { try { _history.Save(); } catch { } });
        if (tick % 5 == 0)
        {
            var df = diskFreeGb;
            _ = Task.Run(() => { try { _alerts.CheckNow(cpuTemp, df, networkUp: true); } catch { } });
        }

        if (tick % 5 == 0)
        {
            _ = Task.Run(() =>
            {
                try
                {
                    var ut = SystemUptime.UptimeText;
                    var pc = System.Diagnostics.Process.GetProcesses().Length;
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        UptimeText = ut;
                        ProcessCount = pc;
                    });
                }
                catch { }
            });
        }

        if (tick % 30 == 0)
        {
            _ = Task.Run(() =>
            {
                try
                {
                    if (!AlertService.IsNetworkUp())
                        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                        {
                            try { _alerts.CheckNow(cpuTemp, AlertService.MinDiskFreeGb(), networkUp: false); }
                            catch { }
                        });
                }
                catch { }
            });
            _ = Task.Run(() =>
            {
                try
                {
                    var samples = _history.Get24h();
                    var fc = HealthPredictor.ForecastCpuTemp(samples);
                    var risk = HealthPredictor.FailureRisk(samples);
                    var anoms = AnomalyDetector.Detect(samples);
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        ForecastText = $"{fc.Verdict} | Risk: {risk}";
                        AnomalyText = anoms.Count == 0
                            ? "Anomalies: none in last 24h"
                            : $"Anomalies: {anoms.Count} (top: {anoms[0].Metric} {anoms[0].Value} z={anoms[0].ZScore} @ {anoms[0].Time:HH:mm})";
                    });
                }
                catch { }
            });
        }

        // Детали железа (WMI — дорого, раз в ~10 минут + первый тик)
        if (_detailsTick == 0 || tick % 300 == 0)
        {
            _detailsTick++;
            _ = Task.Run(() =>
            {
                try
                {
                    var details = DetailedSystemInfoService.FormatSummary();
                    _cachedDetails = details;
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => SystemDetailsText = details);
                }
                catch { }
            });
        }

        var drivesSnapshot = info.Drives;
        var chartTick = tick % 2 == 0; // графики — каждый второй тик (~4с), LiveCharts очень дорогой
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            CpuName = info.CpuName;
            CpuLoad = cpuLoad;
            CpuTemperature = cpuTemp;
            CpuClock = cpuClock;
            CpuPower = cpuPower;

            GpuName = info.GpuName;
            GpuLoad = gpuLoad;
            GpuTemperature = gpuTemp;
            GpuMemoryUsedGb = gpuUsed;
            GpuMemoryTotalGb = gpuTotal;
            GpuMemoryText = $"{gpuUsed:F1} / {gpuTotal:F1} GB";

            MemoryUsedGb = memUsed;
            MemoryTotalGb = memTotal;
            MemoryLoadPercent = memPct;

            NetworkDownloadMbps = netDown;
            NetworkUploadMbps = netUp;

            UpdateDrives(drivesSnapshot);

            if (chartTick)
            {
                AddPoint(_cpuPoints, cpuLoad);
                AddPoint(_gpuPoints, gpuLoad);
                AddPoint(_ramPoints, (float)memPct);
                AddPoint(_netPoints, (float)Math.Max(0, netDown));
            }
        });
    }

    private void AddPoint(ObservableCollection<ObservablePoint> col, double y)
    {
        col.Add(new ObservablePoint(_tick, y));
        while (col.Count > MaxPoints) col.RemoveAt(0);
    }

    private void SelectHistoryRange(string? range)
    {
        if (string.IsNullOrEmpty(range)) return;
        HistoryRange = range;
        Is24h = range == "24h";
        Is7d = range == "7d";
        Is30d = range == "30d";
        var samples = range switch
        {
            "7d" => _history.Get7d(),
            "30d" => _history.Get30d(),
            _ => _history.Get24h()
        };
        var fc = HealthPredictor.ForecastCpuTemp(samples);
        StatusText = samples.Count > 0
            ? $"{range}: {samples.Count} samples, CPU avg {samples.Average(s => s.CpuLoad):F1}% | {fc.Verdict}"
            : $"{range}: no history yet — leave the app running";
    }

    private void ExportReport(string format)
    {
        try
        {
            var samples = HistoryRange switch
            {
                "7d" => _history.Get7d(),
                "30d" => _history.Get30d(),
                _ => _history.Get24h()
            };
            var path = ReportExportService.DefaultPath(HistoryRange, format);
            if (format == "csv") ReportExportService.ExportCsv(samples, path);
            else ReportExportService.ExportJson(samples, path);
            StatusText = $"Report saved: {path} ({samples.Count} samples)";
        }
        catch (Exception ex) { StatusText = $"Export failed: {ex.Message}"; }
    }

    private async Task FreeMemoryAsync()
    {
        StatusText = "Trimming working sets…";
        var n = await MemoryTrimmer.TrimAllAsync();
        StatusText = $"Memory trimmed: {n} processes";
    }

    private void CopySummary()
    {
        try
        {
            var text = $"SystemGuard {DateTime.Now:G}\nOS: {OsName}\nUptime: {UptimeText}\n" +
                       $"CPU: {CpuName} {CpuLoad:F1}% {CpuTemperature:F0}C\n" +
                       $"RAM: {MemoryUsedGb:F1}/{MemoryTotalGb:F1} GB\n" +
                       $"Net: ↓{NetworkDownloadMbps:F2} ↑{NetworkUploadMbps:F2} Mbps\n" +
                       $"{ForecastText}\n{AnomalyText}\n" +
                       $"{(string.IsNullOrEmpty(_cachedDetails) ? SystemDetailsText : _cachedDetails)}\n" +
                       $"BSOD: {BsodService.ListMinidumps().Count} minidumps, {BsodService.FullMemoryDumpStatus()}";
            var path = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                $"SystemGuard_Summary_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
            System.IO.File.WriteAllText(path, text);
            StatusText = $"Summary saved: {path}";
        }
        catch (Exception ex) { StatusText = ex.Message; }
    }

    private void UpdateDrives(System.Collections.Generic.List<DriveInfoModel> driveInfos)
    {
        for (int i = 0; i < driveInfos.Count; i++)
        {
            var info = driveInfos[i];
            if (i < Drives.Count)
            {
                var vm = Drives[i];
                vm.Name = info.Name;
                vm.Temperature = Math.Round(info.Temperature, 1);
                vm.UsedSpace = Math.Round(info.UsedSpace, 2);
                vm.TotalSpace = Math.Round(info.TotalSpace, 2);
                vm.ReadSpeed = Math.Round(info.ReadSpeed / 1024.0, 2);
                vm.WriteSpeed = Math.Round(info.WriteSpeed / 1024.0, 2);
                vm.UsagePercent = info.TotalSpace > 0 ? Math.Round(info.UsedSpace / info.TotalSpace * 100.0, 1) : 0;
            }
            else
            {
                Drives.Add(new StorageDriveViewModel
                {
                    Name = info.Name,
                    Temperature = Math.Round(info.Temperature, 1),
                    UsedSpace = Math.Round(info.UsedSpace, 2),
                    TotalSpace = Math.Round(info.TotalSpace, 2),
                    ReadSpeed = Math.Round(info.ReadSpeed / 1024.0, 2),
                    WriteSpeed = Math.Round(info.WriteSpeed / 1024.0, 2),
                    UsagePercent = info.TotalSpace > 0 ? Math.Round(info.UsedSpace / info.TotalSpace * 100.0, 1) : 0
                });
            }
        }
        while (Drives.Count > driveInfos.Count) Drives.RemoveAt(Drives.Count - 1);
    }
}