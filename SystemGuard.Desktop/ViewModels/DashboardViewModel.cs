using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Platform.Storage;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SystemGuard.Desktop.Models;
using SystemGuard.Desktop.Services;

namespace SystemGuard.Desktop.ViewModels;

public partial class DashboardViewModel : ViewModelBase
{
    private readonly HardwareMonitorService _monitor;
    private readonly MonitoringHistoryService _history = new();

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

    // История / отчёты
    [ObservableProperty] private string _historyRange = "24h";
    [ObservableProperty] private bool _is24h = true;
    [ObservableProperty] private bool _is7d;
    [ObservableProperty] private bool _is30d;
    [ObservableProperty] private string _statusText = "Ready";

    // Продвинутая аналитика: прогноз, аномалии, детали железа (обновляются фоном)
    [ObservableProperty] private string _forecastText = "Forecast: collecting data…";
    [ObservableProperty] private string _anomalyText = "Anomalies: —";
    [ObservableProperty] private string _systemDetailsText = "Details: collecting…";
    private string _cachedDetails = "";
    private int _detailsTick;

    public IRelayCommand<string> SelectHistoryRangeCommand { get; }
    public IAsyncRelayCommand ExportCsvCommand { get; }
    public IAsyncRelayCommand ExportJsonCommand { get; }
    public IAsyncRelayCommand FreeMemoryCommand { get; }
    public IAsyncRelayCommand FlushDnsCommand { get; }
    public IAsyncRelayCommand CopySummaryCommand { get; }

    public Avalonia.Controls.Window? OwnerWindow { get; set; }

    // Ссылки на линии графиков — нужны для динамической окраски
    // (вместо статичного розового цвет зависит от текущей нагрузки).
    private LineSeries<ObservablePoint>? _cpuLine;
    private LineSeries<ObservablePoint>? _gpuLine;
    private LineSeries<ObservablePoint>? _ramLine;
    private LineSeries<ObservablePoint>? _netLine;

    public DashboardViewModel()
    {
        _monitor = new HardwareMonitorService();
        _monitor.OnHardwareUpdated += OnHardwareUpdated;
        OsName = $"{Environment.OSVersion.VersionString}";
        InitCharts();
        SelectHistoryRangeCommand = new RelayCommand<string>(SelectHistoryRange);
        ExportCsvCommand = new AsyncRelayCommand(() => ExportReportAsync("csv"));
        ExportJsonCommand = new AsyncRelayCommand(() => ExportReportAsync("json"));
        FreeMemoryCommand = new AsyncRelayCommand(FreeMemoryAsync);
        FlushDnsCommand = new AsyncRelayCommand(FlushDnsAsync);
        CopySummaryCommand = new AsyncRelayCommand(CopySummaryAsync);
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

        // Разные базовые цвета на каждый график (раньше все 4 были
        // статичным розовым и сливались). Точный оттенок дальше
        // пересчитывается динамически от нагрузки (см. UpdateChartColors).
        var cpu = new SKColor(76, 201, 240);   // cyan-blue
        var gpu = new SKColor(128, 237, 153);  // green
        var ram = new SKColor(184, 146, 255);  // violet
        var net = new SKColor(255, 184, 107);  // amber

        _cpuLine = new LineSeries<ObservablePoint> { Values = _cpuPoints, Stroke = new SolidColorPaint(cpu, 2), Fill = Grad(cpu), GeometrySize = 0, LineSmoothness = 0.65 };
        _gpuLine = new LineSeries<ObservablePoint> { Values = _gpuPoints, Stroke = new SolidColorPaint(gpu, 2), Fill = Grad(gpu), GeometrySize = 0, LineSmoothness = 0.65 };
        _ramLine = new LineSeries<ObservablePoint> { Values = _ramPoints, Stroke = new SolidColorPaint(ram, 2), Fill = Grad(ram), GeometrySize = 0, LineSmoothness = 0.65 };
        _netLine = new LineSeries<ObservablePoint> { Values = _netPoints, Stroke = new SolidColorPaint(net, 2), Fill = Grad(net), GeometrySize = 0, LineSmoothness = 0.65 };

        CpuSeries = new ISeries[] { _cpuLine };
        GpuSeries = new ISeries[] { _gpuLine };
        RamSeries = new ISeries[] { _ramLine };
        NetworkSeries = new ISeries[] { _netLine };

        var t = new SolidColorPaint(SKColors.Transparent);
        var fl = new SolidColorPaint(new SKColor(255, 255, 255, 10));

        XAxes = new[] { new Axis { LabelsPaint = t, SeparatorsPaint = t } };
        YAxes = new[] { new Axis { MinLimit = 0, MaxLimit = 100, LabelsPaint = t, SeparatorsPaint = fl } };
        NetworkYAxes = new[] { new Axis { MinLimit = 0, LabelsPaint = t, SeparatorsPaint = fl } };
    }

    // Светофор для %-метрики: зелёный <50, жёлтый <80, красный >=80.
    private static SKColor TrafficColor(double percent, SKColor baseColor)
    {
        if (percent >= 80) return new SKColor(255, 107, 107); // red
        if (percent >= 50) return new SKColor(255, 209, 102); // yellow
        return baseColor;
    }

    private void UpdateChartColors(double cpuLoad, double gpuLoad, double ramPct, double netMbps)
    {
        try
        {
            static void Recolor(LineSeries<ObservablePoint>? line, SKColor c)
            {
                if (line == null) return;
                line.Stroke = new SolidColorPaint(c, 2);
                line.Fill = new LinearGradientPaint(
                    new SKColor(c.Red, c.Green, c.Blue, 60),
                    new SKColor(c.Red, c.Green, c.Blue, 5),
                    new SKPoint(0.5f, 0), new SKPoint(0.5f, 1));
            }

            Recolor(_cpuLine, TrafficColor(cpuLoad, new SKColor(76, 201, 240)));
            Recolor(_gpuLine, TrafficColor(gpuLoad, new SKColor(128, 237, 153)));
            Recolor(_ramLine, TrafficColor(ramPct, new SKColor(184, 146, 255)));
            // Сеть: пороги по Мбит/с (10/50), иначе линия всегда зелёная на фоне 0-2 Мбит.
            var netBase = new SKColor(255, 184, 107);
            var netColor = netMbps >= 50 ? new SKColor(255, 107, 107)
                : netMbps >= 10 ? new SKColor(255, 209, 102) : netBase;
            Recolor(_netLine, netColor);
        }
        catch { }
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
        // RAM — ТОЛЬКО из GlobalMemoryStatusEx (тот же источник, что Диспетчер задач,
        // /api/status и бот): сумма сенсоров LHM на части машин давала чужой total
        // (дубли/частичные сенсоры), и дашборд расходился с реальностью.
        var (physTotal, physAvail) = Services.DetailedSystemInfoService.GetPhysicalMemory();
        var memTotal = Math.Round(physTotal, 2);
        var memUsed = Math.Round(Math.Max(0, physTotal - physAvail), 2);
        var memPct = memTotal > 0 ? Math.Round(memUsed / memTotal * 100.0, 1) : 0;
        var netDown = Math.Round(info.NetworkDownload / 125_000.0, 2);
        var netUp = Math.Round(info.NetworkUpload / 125_000.0, 2);
        var tick = (int)_tick;

        // История — пишем вне UI-потока.
        // Свободное место берём из DriveInfo (реальные диски), а НЕ из сенсоров
        // LibreHardwareMonitor: там бывают дубли/не те единицы.
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
            UpdateChartColors(cpuLoad, gpuLoad, memPct, Math.Max(0, netDown));
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
        try
        {
            var samples = range switch
            {
                "7d" => _history.Get7d(),
                "30d" => _history.Get30d(),
                _ => _history.Get24h()
            };
            if (samples.Count > 0)
            {
                var fc = HealthPredictor.ForecastCpuTemp(samples);
                var risk = HealthPredictor.FailureRisk(samples);
                var anoms = AnomalyDetector.Detect(samples);
                ForecastText = $"{fc.Verdict} | Risk: {risk}";
                AnomalyText = anoms.Count == 0
                    ? $"Anomalies: none in {range}"
                    : $"Anomalies: {anoms.Count} (top: {anoms[0].Metric} {anoms[0].Value} z={anoms[0].ZScore} @ {anoms[0].Time:HH:mm})";
                StatusText = $"{range}: {samples.Count} samples, CPU avg {samples.Average(s => s.CpuLoad):F1}% | {fc.Verdict}";
                // Показываем выбранный диапазон прямо на графиках (прореживаем до MaxPoints).
                LoadHistoryIntoCharts(samples);
            }
            else
            {
                StatusText = $"{range}: no history yet — leave the app running";
            }
        }
        catch (Exception ex)
        {
            StatusText = $"{range}: {ex.Message}";
        }
    }

    private void LoadHistoryIntoCharts(List<MonitoringSample> samples)
    {
        try
        {
            var step = Math.Max(1, samples.Count / MaxPoints);
            var cpuSel = new List<ObservablePoint>();
            var ramSel = new List<ObservablePoint>();
            var netSel = new List<ObservablePoint>();
            double x = 0;
            for (int i = 0; i < samples.Count; i += step)
            {
                var s = samples[i];
                cpuSel.Add(new ObservablePoint(x, s.CpuLoad));
                ramSel.Add(new ObservablePoint(x, s.RamPercent));
                netSel.Add(new ObservablePoint(x, Math.Max(0, s.NetDownMbps)));
                x += 1;
            }
            _cpuPoints.Clear();
            foreach (var p in cpuSel) _cpuPoints.Add(p);
            // GPU в истории не пишется — оставляем live-линию как есть.
            _ramPoints.Clear();
            foreach (var p in ramSel) _ramPoints.Add(p);
            _netPoints.Clear();
            foreach (var p in netSel) _netPoints.Add(p);
            _tick = Math.Max(_tick, x);
            if (samples.Count > 0)
            {
                var last = samples[samples.Count - 1];
                UpdateChartColors(last.CpuLoad, GpuLoad, last.RamPercent, Math.Max(0, last.NetDownMbps));
            }
        }
        catch { }
    }

    private async Task ExportReportAsync(string format)
    {
        try
        {
            var samples = HistoryRange switch
            {
                "7d" => _history.Get7d(),
                "30d" => _history.Get30d(),
                _ => _history.Get24h()
            };
            if (samples.Count == 0)
            {
                StatusText = $"{HistoryRange}: no history yet — nothing to export, leave the app running";
                return;
            }

            string? targetPath = null;
            string? pickedName = null;
            try
            {
                var top = OwnerWindow != null ? Avalonia.Controls.TopLevel.GetTopLevel(OwnerWindow) : null;
                if (top?.StorageProvider != null)
                {
                    var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    var suggested = $"SystemGuard_{HistoryRange}_{stamp}.{format}";
                    var file = await top.StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
                    {
                        Title = format == "csv" ? "Export monitoring CSV" : "Export monitoring JSON",
                        SuggestedFileName = suggested,
                        FileTypeChoices = new[]
                        {
                            format == "csv"
                                ? new Avalonia.Platform.Storage.FilePickerFileType("CSV file") { Patterns = new[] { "*.csv" } }
                                : new Avalonia.Platform.Storage.FilePickerFileType("JSON file") { Patterns = new[] { "*.json" } }
                        }
                    });
                    if (file == null)
                    {
                        StatusText = "Export cancelled";
                        return;
                    }
                    targetPath = file.TryGetLocalPath() ?? file.Path.ToString();
                    pickedName = file.Name;
                }
            }
            catch { /* fallback ниже */ }

            targetPath ??= ReportExportService.DefaultPath(HistoryRange, format);
            if (format == "csv") ReportExportService.ExportCsv(samples, targetPath);
            else ReportExportService.ExportJson(samples, targetPath);
            StatusText = $"Report saved: {pickedName ?? targetPath} ({samples.Count} samples)";
        }
        catch (Exception ex) { StatusText = $"Export failed: {ex.Message}"; }
    }

    private async Task FreeMemoryAsync()
    {
        StatusText = "Trimming working sets…";
        try
        {
            var (totalBefore, availBefore) = DetailedSystemInfoService.GetPhysicalMemory();
            var usedBefore = Math.Max(0, totalBefore - availBefore);
            var n = await MemoryTrimmer.TrimAllAsync();
            var (totalAfter, availAfter) = DetailedSystemInfoService.GetPhysicalMemory();
            var usedAfter = Math.Max(0, totalAfter - availAfter);
            var freedMb = Math.Max(0, (usedBefore - usedAfter) * 1024);
            StatusText = freedMb >= 1
                ? $"Memory trimmed: {n} processes, freed ~{freedMb:F0} MB ({usedBefore:F1} → {usedAfter:F1} GB)"
                : $"Memory trimmed: {n} processes ({usedAfter:F1}/{totalAfter:F1} GB used)";
        }
        catch (Exception ex)
        {
            StatusText = $"Trim failed: {ex.Message}";
        }
    }

    private async Task FlushDnsAsync()
    {
        StatusText = "Flushing DNS cache…";
        try
        {
            var output = await Task.Run(() =>
            {
                try
                {
                    using var p = new System.Diagnostics.Process();
                    p.StartInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "ipconfig",
                        Arguments = "/flushdns",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                        StandardOutputEncoding = System.Text.Encoding.UTF8,
                        StandardErrorEncoding = System.Text.Encoding.UTF8,
                    };
                    p.Start();
                    var outText = p.StandardOutput.ReadToEnd();
                    var errText = p.StandardError.ReadToEnd();
                    if (!p.WaitForExit(15000)) { try { p.Kill(); } catch { } return "Flush timed out"; }
                    var combined = (outText + " " + errText).Trim();
                    if (p.ExitCode == 0)
                        return string.IsNullOrWhiteSpace(combined) ? "DNS cache flushed" : FirstLine(combined);
                    return string.IsNullOrWhiteSpace(combined) ? $"ipconfig exit {p.ExitCode}" : FirstLine(combined);
                }
                catch (Exception ex) { return ex.Message; }
            });
            StatusText = string.IsNullOrWhiteSpace(output) ? "DNS cache flushed" : output.Trim();
        }
        catch (Exception ex) { StatusText = $"Flush DNS failed: {ex.Message}"; }
    }

    private static string FirstLine(string s)
    {
        var idx = s.IndexOfAny(new[] { '\r', '\n' });
        var line = idx >= 0 ? s.Substring(0, idx) : s;
        return line.Trim().Length > 160 ? line.Trim().Substring(0, 160) : line.Trim();
    }

    private string BuildSummaryText()
    {
        return $"SystemGuard {DateTime.Now:G}\nOS: {OsName}\nUptime: {UptimeText}\n" +
               $"CPU: {CpuName} {CpuLoad:F1}% {CpuTemperature:F0}C\n" +
               $"RAM: {MemoryUsedGb:F1}/{MemoryTotalGb:F1} GB\n" +
               $"Net: ↓{NetworkDownloadMbps:F2} ↑{NetworkUploadMbps:F2} Mbps\n" +
               $"{ForecastText}\n{AnomalyText}\n" +
               $"{(string.IsNullOrEmpty(_cachedDetails) ? SystemDetailsText : _cachedDetails)}\n" +
               $"BSOD: {BsodService.ListMinidumps().Count} minidumps, {BsodService.FullMemoryDumpStatus()}";
    }

    private async Task CopySummaryAsync()
    {
        var text = BuildSummaryText();
        string? savedPath = null;
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                $"SystemGuard_Summary_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
            await File.WriteAllTextAsync(path, text);
            savedPath = path;
        }
        catch (Exception ex)
        {
            StatusText = $"Summary save failed: {ex.Message}";
            return;
        }

        try
        {
            var top = OwnerWindow != null ? Avalonia.Controls.TopLevel.GetTopLevel(OwnerWindow) : null;
            var clipboard = top?.Clipboard ?? OwnerWindow?.Clipboard;
            if (clipboard != null)
            {
                await clipboard.SetTextAsync(text);
                StatusText = $"Summary copied to clipboard + saved: {savedPath}";
                return;
            }
        }
        catch { /* ниже fallback */ }
        StatusText = $"Summary saved (clipboard unavailable): {savedPath}";
    }

    private static bool IsRamLikeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return true;
        var n = name.Trim().ToLowerInvariant();
        // Сюда никогда не должна попадать оперативка (HardwareType.Memory),
        // но если LHM/драйвер обозвал устройство странно — отсекаем явный мусор,
        // кроме Intel Optane Memory (это реальный накопитель).
        if (n.Contains("optane")) return false;
        return n is "memory" or "physical memory" or "generic memory"
            || n.Contains("physical memory") || n.Contains("generic memory");
    }

    private void UpdateDrives(List<DriveInfoModel> driveInfos)
    {
        try
        {
            // 1. Чистим LHM-мусор: пустые имена, полностью нулевые записи и явную RAM.
            var cleanLhm = (driveInfos ?? new List<DriveInfoModel>())
                .Where(d => d != null && !string.IsNullOrWhiteSpace(d.Name) && !IsRamLikeName(d.Name))
                .Where(d => !(d.TotalSpace <= 0 && d.UsedSpace <= 0 && d.Temperature <= 0 && d.ReadSpeed <= 0 && d.WriteSpeed <= 0))
                .Select(d => new DriveInfoModel
                {
                    Name = d.Name.Trim(),
                    Temperature = d.Temperature,
                    UsedSpace = d.UsedSpace,
                    TotalSpace = d.TotalSpace,
                    ReadSpeed = d.ReadSpeed,
                    WriteSpeed = d.WriteSpeed,
                })
                .ToList();

            // 2. Реальные логические диски — источник правды по Used/Total.
            // LHM часто не отдаёт Used/Total для NVMe (нет SMART без админа) —
            // тогда было «Total: 0.0 GB / 0%», хотя скорости R/W ненулевые.
            var logical = new List<DriveInfoModel>();
            try
            {
                foreach (var di in DriveInfo.GetDrives())
                {
                    try
                    {
                        if (!di.IsReady) continue;
                        if (di.DriveType != DriveType.Fixed) continue;
                        double totalGb = di.TotalSize / 1073741824.0;
                        if (totalGb <= 0.5) continue; // recovery-мелочь прячем
                        double freeGb;
                        try { freeGb = di.AvailableFreeSpace / 1073741824.0; }
                        catch { continue; }
                        if (freeGb < 0 || freeGb > totalGb) continue;
                        double usedGb = Math.Max(0, totalGb - freeGb);
                        var letter = di.Name.TrimEnd('\\', '/');
                        var label = "";
                        try { label = di.VolumeLabel ?? ""; } catch { }
                        var display = string.IsNullOrWhiteSpace(label) ? letter : $"{letter} ({label.Trim()})";
                        logical.Add(new DriveInfoModel
                        {
                            Name = display,
                            UsedSpace = (float)usedGb,
                            TotalSpace = (float)totalGb,
                        });
                    }
                    catch { }
                }
            }
            catch { }

            // 3. Обогащаем логические диски температурой/скоростями из LHM.
            // Точное сопоставление физика↔раздел невозможно без WMI-маппинга,
            // поэтому: ищем LHM с близким Total (±15%), иначе берём первый живой.
            DriveInfoModel? fallbackLhm = cleanLhm
                .OrderByDescending(d => Math.Max(d.ReadSpeed, d.WriteSpeed))
                .FirstOrDefault(d => d.Temperature > 0 || d.ReadSpeed > 0 || d.WriteSpeed > 0)
                ?? cleanLhm.FirstOrDefault();
            float fallbackTemp = fallbackLhm?.Temperature ?? 0;
            // Суммарные скорости всех LHM-дисков — честная картина нагрузки СХД,
            // когда разделов несколько, а физика одна.
            float totalRead = cleanLhm.Sum(d => d.ReadSpeed);
            float totalWrite = cleanLhm.Sum(d => d.WriteSpeed);

            var finalList = new List<DriveInfoModel>();
            foreach (var log in logical)
            {
                DriveInfoModel? match = null;
                if (cleanLhm.Count > 0 && log.TotalSpace > 0)
                {
                    match = cleanLhm
                        .Where(d => d.TotalSpace > 0)
                        .OrderBy(d => Math.Abs(d.TotalSpace - log.TotalSpace))
                        .FirstOrDefault();
                    if (match != null && match.TotalSpace > 0
                        && Math.Abs(match.TotalSpace - log.TotalSpace) / log.TotalSpace > 0.35)
                        match = null; // слишком далеко — не выдумываем связь
                }
                finalList.Add(new DriveInfoModel
                {
                    Name = log.Name,
                    UsedSpace = (float)Math.Round(log.UsedSpace, 2),
                    TotalSpace = (float)Math.Round(log.TotalSpace, 2),
                    Temperature = (float)Math.Round(match?.Temperature ?? fallbackTemp, 1),
                    // Скорости LHM в Б/с → КБ/с (как раньше); для раздела
                    // показываем суммарные, чтобы не было «0 при активности».
                    ReadSpeed = (float)Math.Round((match != null && (match.ReadSpeed > 0 || match.WriteSpeed > 0)
                        ? match.ReadSpeed : totalRead) / 1024.0, 2),
                    WriteSpeed = (float)Math.Round((match != null && (match.ReadSpeed > 0 || match.WriteSpeed > 0)
                        ? match.WriteSpeed : totalWrite) / 1024.0, 2),
                });
            }

            // 4. Физические диски LHM, которых нет среди логических
            // (несмонтированные/без буквы), — показываем отдельно, но только
            // если у них есть хоть какие-то данные (не нулевые).
            foreach (var lhm in cleanLhm)
            {
                try
                {
                    if (lhm.TotalSpace > 0 && finalList.Any(f =>
                        Math.Abs(f.TotalSpace - lhm.TotalSpace) / Math.Max(1, f.TotalSpace) < 0.15))
                        continue; // уже покрыт логическим
                    if (lhm.TotalSpace <= 0 && finalList.Count > 0) continue; // скорости уже учтены выше
                    finalList.Add(new DriveInfoModel
                    {
                        Name = lhm.Name,
                        UsedSpace = (float)Math.Round(lhm.UsedSpace, 2),
                        TotalSpace = (float)Math.Round(lhm.TotalSpace, 2),
                        Temperature = (float)Math.Round(lhm.Temperature, 1),
                        ReadSpeed = (float)Math.Round(lhm.ReadSpeed / 1024.0, 2),
                        WriteSpeed = (float)Math.Round(lhm.WriteSpeed / 1024.0, 2),
                    });
                }
                catch { }
            }

            // 5. Льём в коллекцию для UI (% считается в SyncDrivesCollection).
            SyncDrivesCollection(finalList);
        }
        catch
        {
            // Последний шанс: старое поведение 1-в-1, чтобы экран не пустел.
            try { SyncDrivesCollectionLegacy(driveInfos); } catch { }
        }
    }

    private void SyncDrivesCollection(List<DriveInfoModel> finalList)
    {
        for (int i = 0; i < finalList.Count; i++)
        {
            var info = finalList[i];
            var usage = info.TotalSpace > 0 ? Math.Round(info.UsedSpace / info.TotalSpace * 100.0, 1) : 0;
            if (i < Drives.Count)
            {
                var vm = Drives[i];
                vm.Name = info.Name;
                vm.Temperature = info.Temperature;
                vm.UsedSpace = info.UsedSpace;
                vm.TotalSpace = info.TotalSpace;
                vm.ReadSpeed = info.ReadSpeed;
                vm.WriteSpeed = info.WriteSpeed;
                vm.UsagePercent = usage;
            }
            else
            {
                Drives.Add(new StorageDriveViewModel
                {
                    Name = info.Name,
                    Temperature = info.Temperature,
                    UsedSpace = info.UsedSpace,
                    TotalSpace = info.TotalSpace,
                    ReadSpeed = info.ReadSpeed,
                    WriteSpeed = info.WriteSpeed,
                    UsagePercent = usage
                });
            }
        }
        while (Drives.Count > finalList.Count) Drives.RemoveAt(Drives.Count - 1);
        OnPropertyChanged(nameof(HasDrives));
    }

    private void SyncDrivesCollectionLegacy(List<DriveInfoModel> driveInfos)
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
        OnPropertyChanged(nameof(HasDrives));
    }
}