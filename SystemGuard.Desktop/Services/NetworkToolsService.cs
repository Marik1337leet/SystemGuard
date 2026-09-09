using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace SystemGuard.Desktop.Services;

// Сетевые инструменты без новых зависимостей:
// VPN через rasdial, DNS-over-HTTPS (Win11), таблица TCP-соединений
// (учёт активности по приложениям), UPnP-проброс портов через COM.
public class VpnProfile
{
    public string Name { get; set; } = "";
    public bool Connected { get; set; }
}

public static class VpnService
{
    private static string RasPhoneBook =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            @"Microsoft\Network\Connections\Pbk\rasphone.pbk");

    public static List<VpnProfile> ListProfiles()
    {
        var list = new List<VpnProfile>();
        try
        {
            if (File.Exists(RasPhoneBook))
                foreach (var line in File.ReadAllLines(RasPhoneBook))
                {
                    var t = line.Trim();
                    if (t.StartsWith("[") && t.EndsWith("]") && t.Length > 2)
                        list.Add(new VpnProfile { Name = t[1..^1] });
                }
            var connected = ConnectedNames();
            foreach (var p in list) p.Connected = connected.Contains(p.Name);
        }
        catch { }
        return list;
    }

    private static HashSet<string> ConnectedNames()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var out1 = Run("rasdial", "");
            foreach (var line in out1.Split('\n'))
            {
                var m = Regex.Match(line, @"Connected to\s+(.+)", RegexOptions.IgnoreCase);
                if (m.Success) set.Add(m.Groups[1].Value.Trim());
            }
        }
        catch { }
        return set;
    }

    public static string Status() => string.IsNullOrWhiteSpace(Run("rasdial", "")) ? "No VPN connections" : Run("rasdial", "");

    public static string Connect(string name, string? user = null, string? pass = null)
    {
        var args = string.IsNullOrWhiteSpace(user) ? $"\"{name}\"" : $"\"{name}\" {user} {(pass ?? "*")}";
        var out1 = Run("rasdial", args, 30000);
        return string.IsNullOrWhiteSpace(out1) ? $"Connecting to {name}…" : out1.Trim();
    }

    public static string Disconnect(string? name = null)
    {
        var out1 = Run("rasdial", string.IsNullOrWhiteSpace(name) ? "/disconnect" : $"\"{name}\" /disconnect");
        return string.IsNullOrWhiteSpace(out1) ? "Disconnected" : out1.Trim();
    }

    private static string Run(string file, string args, int timeoutMs = 15000)
    {
        try
        {
            var psi = new ProcessStartInfo(file, args)
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            using var p = Process.Start(psi);
            if (p == null) return "";
            var out1 = p.StandardOutput.ReadToEnd();
            p.WaitForExit(timeoutMs);
            return out1.Trim();
        }
        catch { return ""; }
    }
}

public static class DohService
{
    // DNS-over-HTTPS, Windows 11: Set-DnsClientDohServerAddress. На Win10 — честное сообщение.
    public static string Enable(string dnsIp = "1.1.1.1", string dohTemplate = "https://cloudflare-dns.com/dns-query")
    {
        var ps = $"Set-DnsClientDohServerAddress -ServerAddress '{dnsIp}' -DohTemplate '{dohTemplate}' -AllowFallbackToUdp $true -AutoUpgrade $true";
        var out1 = RunPs(ps);
        return string.IsNullOrWhiteSpace(out1) ? $"DoH enabled for {dnsIp}" : out1.Trim();
    }

    public static string Query()
    {
        var out1 = RunPs("Get-DnsClientDohServerAddress | Format-Table ServerAddress,DohTemplate,AutoUpgrade -AutoSize | Out-String");
        return string.IsNullOrWhiteSpace(out1) ? "DoH not configured (needs Windows 11)" : out1.Trim();
    }

    public static string Disable(string dnsIp)
    {
        var out1 = RunPs($"Set-DnsClientDohServerAddress -ServerAddress '{dnsIp}' -AutoUpgrade $false");
        return string.IsNullOrWhiteSpace(out1) ? $"DoH auto-upgrade off for {dnsIp}" : out1.Trim();
    }

    private static string RunPs(string script, int timeoutMs = 20000)
    {
        try
        {
            var psi = new ProcessStartInfo("powershell", $"-NoProfile -Command \"{script}\"")
            { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            using var p = Process.Start(psi);
            if (p == null) return "";
            var out1 = p.StandardOutput.ReadToEnd();
            var err = p.StandardError.ReadToEnd();
            p.WaitForExit(timeoutMs);
            return string.IsNullOrWhiteSpace(err) ? out1 : err;
        }
        catch (Exception ex) { return ex.Message; }
    }
}

public class TcpConnectionEntry
{
    public string Proto { get; set; } = "";
    public string Local { get; set; } = "";
    public string Remote { get; set; } = "";
    public string State { get; set; } = "";
    public int Pid { get; set; }
    public string Process { get; set; } = "";
}

public class AppTrafficSummary
{
    public string App { get; set; } = "";
    public int Connections { get; set; }
    public int Established { get; set; }
    public List<string> TopRemotes { get; set; } = new();
}

public static class TcpConnectionsService
{
    // Активные соединения через netstat -ano + имена процессов.
    // Байты на процесс без ETW-драйвера недоступны — даём честную аппроксимацию:
    // число соединений / ESTABLISHED на приложение + топ удалённых адресов.
    public static List<TcpConnectionEntry> GetConnections()
    {
        var list = new List<TcpConnectionEntry>();
        try
        {
            var psi = new ProcessStartInfo("netstat", "-ano -p TCP")
            { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
            using var p = Process.Start(psi);
            if (p == null) return list;
            var out1 = p.StandardOutput.ReadToEnd();
            p.WaitForExit(15000);
            foreach (var raw in out1.Split('\n'))
            {
                var parts = raw.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 5 || !parts[0].Equals("TCP", StringComparison.OrdinalIgnoreCase)) continue;
                if (!int.TryParse(parts[4], out var pid)) continue;
                list.Add(new TcpConnectionEntry
                {
                    Proto = "TCP", Local = parts[1], Remote = parts[2],
                    State = parts[3], Pid = pid, Process = ProcName(pid)
                });
            }
        }
        catch { }
        return list;
    }

    private static readonly Dictionary<int, string> _procCache = new();
    private static string ProcName(int pid)
    {
        lock (_procCache)
        {
            if (_procCache.TryGetValue(pid, out var n)) return n;
            try { n = Process.GetProcessById(pid).ProcessName; }
            catch { n = $"PID {pid}"; }
            _procCache[pid] = n;
            if (_procCache.Count > 2000) _procCache.Clear();
            return n;
        }
    }

    public static List<AppTrafficSummary> SummarizeByApp()
    {
        return GetConnections()
            .GroupBy(c => c.Process)
            .Select(g => new AppTrafficSummary
            {
                App = g.Key,
                Connections = g.Count(),
                Established = g.Count(c => c.State.Equals("ESTABLISHED", StringComparison.OrdinalIgnoreCase)),
                TopRemotes = g.GroupBy(c => c.Remote).OrderByDescending(x => x.Count())
                    .Take(3).Select(x => $"{x.Key} ×{x.Count()}").ToList()
            })
            .OrderByDescending(s => s.Established).ThenByDescending(s => s.Connections)
            .Take(50).ToList();
    }
}

public static class UpnpService
{
    // Проброс портов через UPnP IGD (COM UPnPNAT). Работает только если роутер с UPnP.
    public static string ListMappings()
    {
        try
        {
            var t = Type.GetTypeFromProgID("HNetCfg.NATUPnP");
            if (t == null) return "UPnP COM unavailable";
            dynamic nat = Activator.CreateInstance(t)!;
            dynamic mappings = nat.StaticPortMappingCollection;
            if (mappings == null) return "No UPnP gateway found";
            var lines = new List<string>();
            foreach (dynamic m in mappings)
                lines.Add($"{m.Protocol}/{m.ExternalPort} → {m.InternalClient}:{m.InternalPort} ({m.Description})");
            return lines.Count == 0 ? "No UPnP mappings" : string.Join("\n", lines);
        }
        catch (Exception ex) { return $"UPnP unavailable: {ex.Message}"; }
    }

    public static string AddMapping(int externalPort, int internalPort, string internalClient, string protocol = "TCP", string desc = "SystemGuard")
    {
        try
        {
            var t = Type.GetTypeFromProgID("HNetCfg.NATUPnP");
            if (t == null) return "UPnP COM unavailable";
            dynamic nat = Activator.CreateInstance(t)!;
            dynamic mappings = nat.StaticPortMappingCollection;
            if (mappings == null) return "No UPnP gateway found";
            mappings.Add(externalPort, protocol.ToUpper(), internalPort, internalClient, true, desc);
            return $"Mapped {protocol}/{externalPort} → {internalClient}:{internalPort}";
        }
        catch (Exception ex) { return $"UPnP map failed: {ex.Message}"; }
    }

    public static string RemoveMapping(int externalPort, string protocol = "TCP")
    {
        try
        {
            var t = Type.GetTypeFromProgID("HNetCfg.NATUPnP");
            if (t == null) return "UPnP COM unavailable";
            dynamic nat = Activator.CreateInstance(t)!;
            dynamic mappings = nat.StaticPortMappingCollection;
            if (mappings == null) return "No UPnP gateway found";
            mappings.Remove(externalPort, protocol.ToUpper());
            return $"Unmapped {protocol}/{externalPort}";
        }
        catch (Exception ex) { return $"UPnP unmap failed: {ex.Message}"; }
    }
}

public class SpeedServerResult
{
    public string Server { get; set; } = "";
    public bool Ok { get; set; }
    public double Mbps { get; set; }
    public string Detail { get; set; } = "";
}

public static class MultiSpeedTestService
{
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(60) };

    public static async Task<List<SpeedServerResult>> TestAllAsync()
    {
        var results = new List<SpeedServerResult>();
        foreach (var (name, url) in NetworkService.SpeedServers)
            results.Add(await TestOneAsync(name, url));
        return results;
    }

    public static async Task<SpeedServerResult> TestOneAsync(string name, string url)
    {
        try
        {
            var sw = Stopwatch.StartNew();
            var data = await _http.GetByteArrayAsync(url);
            sw.Stop();
            if (data.Length == 0) return new SpeedServerResult { Server = name, Detail = "Empty response" };
            return new SpeedServerResult
            {
                Server = name, Ok = true,
                Mbps = Math.Round(data.Length * 8.0 / sw.Elapsed.TotalSeconds / 1_000_000.0, 1),
                Detail = $"{data.Length / 1048576} MB in {sw.Elapsed.TotalSeconds:F1}s"
            };
        }
        catch (Exception ex) { return new SpeedServerResult { Server = name, Detail = ex.Message }; }
    }
}
