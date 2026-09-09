using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace SystemGuard.Desktop.Services;

public class NetworkAdapterInfo
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Type { get; set; } = "";
    public string Status { get; set; } = "";
    public bool IsConnected { get; set; }
    public string IpAddress { get; set; } = "";
    public string MacAddress { get; set; } = "";
    public string Gateway { get; set; } = "";
    public string DnsServers { get; set; } = "";
    public string SubnetMask { get; set; } = "";
    public long Speed { get; set; }
    public string SpeedText => Speed >= 1_000_000_000 ? $"{Speed / 1_000_000_000.0:F1} Gbps" :
                               Speed >= 1_000_000 ? $"{Speed / 1_000_000.0:F0} Mbps" :
                               $"{Speed / 1000.0:F0} Kbps";
}

public class NetworkTraffic
{
    public string AdapterName { get; set; } = "";
    public double DownloadMbps { get; set; }
    public double UploadMbps { get; set; }
    public long TotalDownloaded { get; set; }
    public long TotalUploaded { get; set; }
}

public class FirewallRule
{
    public string Name { get; set; } = "";
    public string Direction { get; set; } = "";
    public string Action { get; set; } = "";
    public string Protocol { get; set; } = "";
    public string Port { get; set; } = "";
    public bool IsEnabled { get; set; }
}

public class NetworkService
{
    private static readonly System.Net.Http.HttpClient _sharedHttp = new()
    {
        Timeout = TimeSpan.FromSeconds(5)
    };

    private long _lastDownload;
    private long _lastUpload;
    private DateTime _lastCheck = DateTime.Now;

    public List<NetworkAdapterInfo> GetAdapters()
    {
        var adapters = new List<NetworkAdapterInfo>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            try
            {
                var ipProps = nic.GetIPProperties();
                var ipv4 = ipProps.UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork);
                var gateway = ipProps.GatewayAddresses.FirstOrDefault();
                var dns = string.Join(", ", ipProps.DnsAddresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork).Select(a => a.ToString()));

                adapters.Add(new NetworkAdapterInfo
                {
                    Name = nic.Name,
                    Description = nic.Description,
                    Type = nic.NetworkInterfaceType.ToString(),
                    Status = nic.OperationalStatus.ToString(),
                    IsConnected = nic.OperationalStatus == OperationalStatus.Up,
                    IpAddress = ipv4?.Address.ToString() ?? "N/A",
                    MacAddress = nic.GetPhysicalAddress().ToString(),
                    Gateway = gateway?.Address.ToString() ?? "N/A",
                    DnsServers = string.IsNullOrEmpty(dns) ? "N/A" : dns,
                    SubnetMask = ipv4?.IPv4Mask?.ToString() ?? "N/A",
                    Speed = nic.Speed
                });
            }
            catch { }
        }
        return adapters;
    }

    public string GetPublicIp()
    {
        try
        {
            return _sharedHttp.GetStringAsync("https://api.ipify.org")
                .ConfigureAwait(false).GetAwaiter().GetResult().Trim();
        }
        catch { return "Unknown"; }
    }

    public async Task<string> GetPublicIpAsync()
    {
        try
        {
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(6));
            var s = await _sharedHttp.GetStringAsync("https://api.ipify.org", cts.Token)
                .ConfigureAwait(false);
            return s.Trim();
        }
        catch { return "Unknown"; }
    }

    public string GetExternalIp() => GetPublicIp();

    public NetworkTraffic GetTraffic()
    {
        var now = DateTime.Now;
        var elapsed = (now - _lastCheck).TotalSeconds;
        if (elapsed < 0.1) elapsed = 1;

        long totalDown = 0, totalUp = 0;
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            var stats = nic.GetIPStatistics();
            totalDown += stats.BytesReceived;
            totalUp += stats.BytesSent;
        }

        var downMbps = (totalDown - _lastDownload) * 8.0 / elapsed / 1_000_000;
        var upMbps = (totalUp - _lastUpload) * 8.0 / elapsed / 1_000_000;

        _lastDownload = totalDown;
        _lastUpload = totalUp;
        _lastCheck = now;

        return new NetworkTraffic
        {
            DownloadMbps = Math.Round(downMbps, 2),
            UploadMbps = Math.Round(upMbps, 2),
            TotalDownloaded = totalDown,
            TotalUploaded = totalUp
        };
    }

    public List<FirewallRule> GetFirewallRules(int maxRules = 500)
    {
        // Первично — COM HNetCfg.FwPolicy2 (языконезависимый, без парсинга).
        // netsh — только fallback (его вывод локализован + OEM-кодировка).
        try
        {
            var t = Type.GetTypeFromProgID("HNetCfg.FwPolicy2");
            if (t != null)
            {
                dynamic policy = Activator.CreateInstance(t)!;
                var rules = new List<FirewallRule>();
                foreach (dynamic r in policy.Rules)
                {
                    try
                    {
                        rules.Add(new FirewallRule
                        {
                            Name = (string)r.Name,
                            Direction = (int)r.Direction == 1 ? "In" : "Out",
                            Action = (int)r.Action == 1 ? "Allow" : "Block",
                            Protocol = ((int)r.Protocol) switch { 6 => "TCP", 17 => "UDP", 1 => "ICMPv4", 58 => "ICMPv6", 256 => "Any", var p => $"Proto {p}" },
                            Port = (string)(r.LocalPorts ?? ""),
                            IsEnabled = (bool)r.Enabled
                        });
                    }
                    catch { }
                    if (rules.Count >= maxRules) break;
                }
                if (rules.Count > 0) return rules;
            }
        }
        catch { }
        return GetFirewallRulesViaNetsh(maxRules);
    }

    private static List<FirewallRule> GetFirewallRulesViaNetsh(int maxRules)
    {
        var rules = new List<FirewallRule>();
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = "advfirewall firewall show rule name=all",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
                // Вывод netsh — в OEM-кодировке консоли; без этого кириллица бьётся
                StandardOutputEncoding = GetOemEncoding()
            };
            var process = Process.Start(psi);
            if (process == null) return rules;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(30000);

            // netsh локализован: на русской Windows метки русские.
            // Парсим оба варианта, значение — всё после ПЕРВОГО двоеточия.
            static string Val(string line)
            {
                var i = line.IndexOf(':');
                return i >= 0 ? line[(i + 1)..].Trim() : "";
            }
            static bool Is(string line, string en, params string[] ru)
            {
                if (line.Contains(en, StringComparison.OrdinalIgnoreCase)) return true;
                foreach (var r in ru)
                    if (line.Contains(r, StringComparison.OrdinalIgnoreCase)) return true;
                return false;
            }

            FirewallRule? current = null;
            foreach (var line in output.Split('\n').Take(6000))
            {
                if (Is(line, "Rule Name:", "Имя правила:"))
                {
                    if (current != null)
                    {
                        rules.Add(current);
                        if (rules.Count >= maxRules) break;
                    }
                    current = new FirewallRule { Name = Val(line) };
                }
                else if (current != null)
                {
                    if (Is(line, "Direction:", "Направление:")) current.Direction = Val(line);
                    else if (Is(line, "Action:", "Действие:")) current.Action = Val(line);
                    else if (Is(line, "Protocol:", "Протокол:")) current.Protocol = Val(line);
                    else if (Is(line, "LocalPort:", "Локальный порт:") || Is(line, "RemotePort:", "Удаленный порт:", "Удалённый порт:"))
                    {
                        var v = Val(line);
                        if (!string.IsNullOrEmpty(v)) current.Port = v;
                    }
                    else if (Is(line, "Enabled:", "Включен:", "Включено:"))
                        current.IsEnabled = Val(line).Contains("Yes", StringComparison.OrdinalIgnoreCase)
                            || Val(line).Contains("Да", StringComparison.OrdinalIgnoreCase);
                }
            }
            if (current != null && rules.Count < maxRules) rules.Add(current);
        }
        catch { }
        return rules;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern uint GetConsoleCP();

    private static System.Text.Encoding GetOemEncoding()
    {
        try
        {
            var cp = GetConsoleCP();
            if (cp != 0) return System.Text.Encoding.GetEncoding((int)cp);
        }
        catch { }
        return System.Text.Encoding.UTF8;
    }

    public void FlushDns()
    {
        try { System.Diagnostics.Process.Start("ipconfig", "/flushdns"); } catch { }
    }

    // Быстрый замер скорости: скачивание 10 МБ с CDN, замер времени.
    // Честный тест последней мили без сторонних библиотек.
    public async Task<(bool Ok, double Mbps, string Detail)> TestDownloadSpeedAsync()
    {
        const string url = "https://cachefly.cachefly.net/10mb.test";
        try
        {
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var data = await http.GetByteArrayAsync(url);
            sw.Stop();
            if (data.Length == 0 || sw.Elapsed.TotalSeconds <= 0)
                return (false, 0, "Empty response");
            double mbps = data.Length * 8.0 / sw.Elapsed.TotalSeconds / 1_000_000.0;
            return (true, Math.Round(mbps, 1),
                $"{data.Length / 1048576} MB in {sw.Elapsed.TotalSeconds:F1}s");
        }
        catch (Exception ex)
        {
            return (false, 0, ex.Message);
        }
    }

    public void ResetNetwork()
    {
        try
        {
            Process.Start("ipconfig", "/release")?.WaitForExit(5000);
            Process.Start("ipconfig", "/renew")?.WaitForExit(5000);
            Process.Start("ipconfig", "/flushdns")?.WaitForExit(3000);
        }
        catch { }
    }

    public async Task<double> TestLatency(string host = "8.8.8.8")
    {
        try
        {
            var ping = new Ping();
            var reply = await ping.SendPingAsync(host, 2000);
            return reply.Status == IPStatus.Success ? Math.Round((double)reply.RoundtripTime, 1) : -1;
        }
        catch { return -1; }
    }

    // Ping + джиттер + потери: N замеров, возвращаем (avgMs, jitterMs, lossPercent).
    public async Task<(double AvgMs, double JitterMs, double LossPercent)> MeasureQualityAsync(
        string host = "8.8.8.8", int probes = 10)
    {
        probes = Math.Clamp(probes, 3, 50);
        var rtts = new List<double>();
        int lost = 0;
        try
        {
            using var ping = new Ping();
            for (int i = 0; i < probes; i++)
            {
                try
                {
                    var reply = await ping.SendPingAsync(host, 2000);
                    if (reply.Status == IPStatus.Success) rtts.Add(reply.RoundtripTime);
                    else lost++;
                }
                catch { lost++; }
                await Task.Delay(120);
            }
        }
        catch { }
        if (rtts.Count == 0) return (-1, -1, 100);
        double avg = rtts.Average();
        double jitter = rtts.Count > 1
            ? rtts.Zip(rtts.Skip(1), (a, b) => Math.Abs(b - a)).Average()
            : 0;
        double loss = Math.Round(lost * 100.0 / probes, 1);
        return (Math.Round(avg, 1), Math.Round(jitter, 1), loss);
    }

    public static string NetworkQualitySummary(double avgMs, double jitterMs, double loss)
    {
        if (avgMs < 0) return "offline";
        if (loss > 10 || avgMs > 300) return "Poor";
        if (loss > 2 || avgMs > 120 || jitterMs > 40) return "Average";
        if (avgMs > 50 || jitterMs > 15) return "Good";
        return "Excellent";
    }

    // Замер скорости с выбором сервера (Cloudflare / Cachefly 10MB).
    public async Task<(bool Ok, double Mbps, string Detail)> TestDownloadSpeedOnAsync(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) url = "https://cachefly.cachefly.net/10mb.test";
        try
        {
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var data = await http.GetByteArrayAsync(url);
            sw.Stop();
            if (data.Length == 0 || sw.Elapsed.TotalSeconds <= 0)
                return (false, 0, "Empty response");
            double mbps = data.Length * 8.0 / sw.Elapsed.TotalSeconds / 1_000_000.0;
            return (true, Math.Round(mbps, 1), $"{data.Length / 1048576} MB in {sw.Elapsed.TotalSeconds:F1}s from {new Uri(url).Host}");
        }
        catch (Exception ex) { return (false, 0, ex.Message); }
    }

    public static readonly IReadOnlyList<(string Name, string Url)> SpeedServers = new List<(string, string)>
    {
        ("Cloudflare (5MB)", "https://speed.cloudflare.com/__down?bytes=5000000"),
        ("Cachefly (10MB)", "https://cachefly.cachefly.net/10mb.test"),
    };

    // Блокировка приложения в брандмауэре (исходящие) + снятие блокировки.
    // Возвращаем реальный вывод netsh, а не слепое "готово".
    public static string AddFirewallBlockRule(string ruleName, string appPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(appPath)) return "No app path";
            var psi = new ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = $"advfirewall firewall add rule name=\"SG Block {ruleName}\" dir=out program=\"{appPath}\" action=block enable=yes",
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            var out1 = (p?.StandardOutput.ReadToEnd() ?? "") + (p?.StandardError.ReadToEnd() ?? "");
            p?.WaitForExit(10000);
            out1 = out1.Trim();
            if (p != null && p.ExitCode != 0) return $"Block failed: {out1}";
            return string.IsNullOrWhiteSpace(out1) ? $"Blocked outbound: {ruleName}" : out1;
        }
        catch (Exception ex) { return $"Block failed: {ex.Message}"; }
    }

    public static string RemoveFirewallBlockRule(string ruleName)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = $"advfirewall firewall delete rule name=\"SG Block {ruleName}\"",
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            var out1 = (p?.StandardOutput.ReadToEnd() ?? "") + (p?.StandardError.ReadToEnd() ?? "");
            p?.WaitForExit(10000);
            return string.IsNullOrWhiteSpace(out1.Trim()) ? $"Unblocked: {ruleName}" : out1.Trim();
        }
        catch (Exception ex) { return $"Unblock failed: {ex.Message}"; }
    }
}