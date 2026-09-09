using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace SystemGuard.Desktop.Services;

// Переключатель DNS: Cloudflare / Google / OpenDNS / Custom. Реально применяет через netsh.
public class DnsService
{
    public record DnsOption(string Name, string Primary, string Secondary);

    public static readonly List<DnsOption> Options = new()
    {
        new("Automatic (DHCP)", "", ""),
        new("Cloudflare", "1.1.1.1", "1.0.0.1"),
        new("Google", "8.8.8.8", "8.8.4.4"),
        new("OpenDNS", "208.67.222.222", "208.67.220.220"),
    };

    public static string Apply(string adapterName, string primary, string secondary)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(adapterName)) return "No adapter selected";
            if (string.IsNullOrWhiteSpace(primary))
            {
                RunNetsh($"interface ip set dns name=\"{adapterName}\" source=dhcp");
                return $"DNS reset to DHCP on {adapterName}";
            }
            RunNetsh($"interface ip set dns name=\"{adapterName}\" static {primary}");
            if (!string.IsNullOrWhiteSpace(secondary))
                RunNetsh($"interface ip add dns name=\"{adapterName}\" {secondary} index=2");
            return $"DNS set to {primary} on {adapterName}";
        }
        catch (Exception ex) { return $"DNS failed: {ex.Message}"; }
    }

    public static List<string> AdapterNames()
    {
        try
        {
            return System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up)
                .Select(n => n.Name).ToList();
        }
        catch { return new List<string>(); }
    }

    private static void RunNetsh(string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "netsh", Arguments = args,
            UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true
        };
        using var p = Process.Start(psi);
        p?.WaitForExit(10000);
    }
}
