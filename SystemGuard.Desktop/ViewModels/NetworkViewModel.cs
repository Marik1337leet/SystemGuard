// ВАЖНО: этот файл полностью ЗАМЕНЯЕТ старый NetworkViewModel.cs в проекте.
// Удали старый файл из ViewModels/, иначе будет конфликт partial-полей
// (та же причина ошибок SelectTabCommand/FirewallRules/LatencyText "does not exist").
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using SystemGuard.Desktop.Services;

namespace SystemGuard.Desktop.ViewModels;

public partial class NetworkViewModel : ViewModelBase
{
    private readonly NetworkService _networkService = new();
    private Timer? _trafficTimer;

    [ObservableProperty] private ObservableCollection<NetworkAdapterInfo> _adapters = new();
    [ObservableProperty] private ObservableCollection<FirewallRule> _firewallRules = new();
    [ObservableProperty] private NetworkTraffic? _traffic;
    [ObservableProperty] private string _externalIp = "Loading...";
    [ObservableProperty] private string _statusText = "Ready";
    [ObservableProperty] private double _latency;
    [ObservableProperty] private string _latencyText = "Ping: —";
    [ObservableProperty] private string _selectedTab = "Adapters";
    [ObservableProperty] private bool _isAdaptersTab = true;
    [ObservableProperty] private bool _isFirewallTab;
    [ObservableProperty] private bool _isToolsTab;
    [ObservableProperty] private string _speedTestResult = "";
    [ObservableProperty] private bool _isTestingSpeed;
    [ObservableProperty] private double _speedTestProgress;

    // Tools: DNS / hosts / ports — всё реальное
    [ObservableProperty] private string _selectedDnsOption = "Automatic (DHCP)";
    [ObservableProperty] private string _customDns = "";
    [ObservableProperty] private string _selectedAdapter = "";
    [ObservableProperty] private string _hostsText = "";
    [ObservableProperty] private string _portHost = "127.0.0.1";
    [ObservableProperty] private int _portFrom = 80;
    [ObservableProperty] private int _portTo = 90;
    [ObservableProperty] private string _openPortsText = "";
    [ObservableProperty] private bool _isScanningPorts;
    [ObservableProperty] private ObservableCollection<WifiNetwork> _wifiNetworks = new();
    [ObservableProperty] private string _monthTrafficText = "";
    [ObservableProperty] private string _wolMac = "";
    [ObservableProperty] private bool _isScanningWifi;

    private readonly TrafficAccountingService _trafficAccounting = new();

    public string[] DnsOptions => DnsService.Options.Select(o => o.Name).ToArray();

    public IRelayCommand<string> SelectTabCommand { get; }
    public IRelayCommand RefreshCommand { get; }
    public IRelayCommand FlushDnsCommand { get; }
    public IRelayCommand ResetNetworkCommand { get; }
    public IRelayCommand TestLatencyCommand { get; }
    public IRelayCommand TestSpeedCommand { get; }
    public IRelayCommand ApplyDnsCommand { get; }
    public IRelayCommand LoadHostsCommand { get; }
    public IRelayCommand SaveHostsCommand { get; }
    public IRelayCommand AddAdblockCommand { get; }
    public IRelayCommand ScanPortsCommand { get; }
    public IRelayCommand ScanWifiCommand { get; }
    public IRelayCommand WakeCommand { get; }

    public NetworkViewModel()
    {
        SelectTabCommand = new RelayCommand<string>(SelectTab);
        RefreshCommand = new AsyncRelayCommand(LoadAdaptersAsync);
        FlushDnsCommand = new RelayCommand(FlushDns);
        ResetNetworkCommand = new AsyncRelayCommand(ResetNetworkAsync);
        TestLatencyCommand = new AsyncRelayCommand(TestLatencyAsync);
        TestSpeedCommand = new AsyncRelayCommand(TestSpeedAsync);
        ApplyDnsCommand = new RelayCommand(ApplyDns);
        LoadHostsCommand = new RelayCommand(() => HostsText = HostsEditorService.Read());
        SaveHostsCommand = new RelayCommand(() => StatusText = HostsEditorService.Save(HostsText));
        AddAdblockCommand = new RelayCommand(() => HostsText = HostsEditorService.AddAdblockRules(HostsText));
        ScanPortsCommand = new AsyncRelayCommand(ScanPortsAsync);
        ScanWifiCommand = new AsyncRelayCommand(ScanWifiAsync);
        WakeCommand = new RelayCommand(() => StatusText = WakeOnLanService.Send(WolMac.Trim()));
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    public override void OnActivated()
    {
        base.OnActivated();
        _ = LoadAdaptersAsync();
        _ = LoadExternalIpAsync();

        // Обновление трафика каждые 2 секунды; сам замер — в фоне,
        // в UI уходит только готовый результат (раньше GetAllNetworkInterfaces
        // выполнялся прямо в UI-потоке и подвешивал окно каждую секунду)
        _trafficTimer = new Timer(
            _ => _ = Task.Run(() =>
            {
                NetworkTraffic? t = null;
                try { t = _networkService.GetTraffic(); } catch { return; }
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    Traffic = t;
                    if (t != null)
                    {
                        try
                        {
                            _trafficAccounting.Record(t.TotalDownloaded, t.TotalUploaded);
                            var (md, mu) = _trafficAccounting.CurrentMonthTotal();
                            MonthTrafficText = $"Month: ↓{md / 1073741824.0:F2} GB ↑{mu / 1073741824.0:F2} GB";
                        }
                        catch { }
                    }
                });
            }),
            null, TimeSpan.Zero, TimeSpan.FromSeconds(2));
    }

    public override void OnDeactivated()
    {
        base.OnDeactivated();
        _trafficTimer?.Dispose();
        _trafficTimer = null;
    }

    // ── Tab ───────────────────────────────────────────────────────────────────

    private void SelectTab(string? tab)
    {
        if (string.IsNullOrEmpty(tab)) return;
        SelectedTab = tab;
        IsAdaptersTab = tab == "Adapters";
        IsFirewallTab = tab == "Firewall";
        IsToolsTab = tab == "Tools";

        if (IsFirewallTab && FirewallRules.Count == 0)
            _ = LoadFirewallAsync();
        if (IsToolsTab && string.IsNullOrEmpty(HostsText))
            HostsText = HostsEditorService.Read();
    }

    // ── Data loading ──────────────────────────────────────────────────────────

    private async Task LoadAdaptersAsync()
    {
        var adapters = await Task.Run(() => _networkService.GetAdapters());
        Adapters = new ObservableCollection<NetworkAdapterInfo>(adapters);
        if (string.IsNullOrEmpty(SelectedAdapter) && adapters.Count > 0)
            SelectedAdapter = adapters.FirstOrDefault(a => a.IsConnected)?.Name ?? adapters[0].Name;
        StatusText = $"{adapters.Count} adapters found";
    }

    private async Task LoadExternalIpAsync()
    {
        ExternalIp = "Loading...";
        ExternalIp = await _networkService.GetPublicIpAsync();
    }

    private void RefreshTraffic()
    {
        Traffic = _networkService.GetTraffic();
        if (Traffic != null)
        {
            _trafficAccounting.Record(Traffic.TotalDownloaded, Traffic.TotalUploaded);
            var (md, mu) = _trafficAccounting.CurrentMonthTotal();
            MonthTrafficText = $"Month: ↓{md / 1073741824.0:F2} GB ↑{mu / 1073741824.0:F2} GB";
        }
    }

    private async Task LoadFirewallAsync()
    {
        StatusText = "Loading firewall rules...";
        var rules = await Task.Run(() => _networkService.GetFirewallRules());
        FirewallRules = new ObservableCollection<FirewallRule>(rules);
        StatusText = $"{rules.Count} firewall rules";
    }

    // ── Actions ───────────────────────────────────────────────────────────────

    private void FlushDns()
    {
        _networkService.FlushDns();
        StatusText = "DNS cache flushed";
    }

    private async Task ResetNetworkAsync()
    {
        StatusText = "Resetting network...";
        await Task.Run(() => _networkService.ResetNetwork());
        await LoadAdaptersAsync();
        StatusText = "Network reset complete";
    }

    private async Task TestLatencyAsync()
    {
        LatencyText = "Pinging...";
        Latency = await _networkService.TestLatency();
        LatencyText = Latency > 0
            ? $"Ping: {Latency} ms"
            : "Ping blocked (ICMP filtered?) — not an internet problem";
        StatusText = LatencyText;
    }

    private async Task TestSpeedAsync()
    {
        if (IsTestingSpeed) return;
        IsTestingSpeed = true;
        SpeedTestProgress = 0;
        SpeedTestResult = "Connecting…";
        try
        {
            var progress = new Progress<(double Percent, double Mbps)>(p =>
            {
                SpeedTestProgress = p.Percent;
                if (p.Mbps > 0)
                    SpeedTestResult = $"Testing… {p.Mbps:F1} Mbps ({p.Percent:F0}%)";
            });
            var (ok, mbps, detail) = await SpeedTestService.RunDownloadTestAsync(progress);
            SpeedTestProgress = ok ? 100 : 0;
            SpeedTestResult = ok ? $"Download: {mbps:F1} Mbps ({detail})" : $"Speed test failed: {detail}";
            StatusText = SpeedTestResult;
        }
        catch (Exception ex)
        {
            SpeedTestProgress = 0;
            SpeedTestResult = $"Speed test failed: {ex.Message}";
            StatusText = SpeedTestResult;
        }
        finally { IsTestingSpeed = false; }
    }

    private void ApplyDns()
    {
        var opt = DnsService.Options.FirstOrDefault(o => o.Name == SelectedDnsOption);
        string primary = opt?.Primary ?? "", secondary = opt?.Secondary ?? "";
        if (!string.IsNullOrWhiteSpace(CustomDns))
        {
            var parts = CustomDns.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries);
            primary = parts.Length > 0 ? parts[0] : "";
            secondary = parts.Length > 1 ? parts[1] : "";
        }
        StatusText = DnsService.Apply(SelectedAdapter, primary, secondary);
    }

    private async Task ScanPortsAsync()
    {
        if (IsScanningPorts) return;
        IsScanningPorts = true;
        OpenPortsText = $"Scanning {PortHost} {PortFrom}-{PortTo}…";
        try
        {
            var open = await PortScannerService.ScanAsync(PortHost.Trim(), PortFrom, PortTo);
            OpenPortsText = open.Count > 0 ? $"Open: {string.Join(", ", open.Select(r => r.Port))}" : "No open ports in range";
            StatusText = OpenPortsText;
        }
        catch (Exception ex) { OpenPortsText = ex.Message; }
        finally { IsScanningPorts = false; }
    }

    private async Task ScanWifiAsync()
    {
        if (IsScanningWifi) return;
        IsScanningWifi = true;
        StatusText = "Scanning Wi-Fi…";
        try
        {
            var nets = await Task.Run(() => WifiScannerService.Scan());
            WifiNetworks = new ObservableCollection<WifiNetwork>(nets);
            StatusText = nets.Count > 0 ? $"{nets.Count} Wi-Fi networks" : "No Wi-Fi networks found";
        }
        catch (Exception ex) { StatusText = ex.Message; }
        finally { IsScanningWifi = false; }
    }
}
