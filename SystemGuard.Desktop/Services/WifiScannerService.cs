using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace SystemGuard.Desktop.Services;

public class WifiNetwork
{
    public string Ssid { get; set; } = "";
    public string Signal { get; set; } = "";
    public string Auth { get; set; } = "";
    public string Channel { get; set; } = "";
}

// Анализ Wi-Fi сетей через netsh (каналы выводятся отдельным запросом на интерфейс).
public static class WifiScannerService
{
    public static List<WifiNetwork> Scan()
    {
        var list = new List<WifiNetwork>();
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "netsh", Arguments = "wlan show networks mode=bssid",
                UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p == null) return list;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(15000);

            WifiNetwork? cur = null;
            foreach (var raw in output.Split('\n'))
            {
                var line = raw.Trim();
                if (line.StartsWith("SSID") && line.Contains(":"))
                {
                    if (cur != null) list.Add(cur);
                    cur = new WifiNetwork { Ssid = line[(line.IndexOf(':') + 1)..].Trim() };
                }
                else if (cur != null)
                {
                    if (line.StartsWith("Signal")) cur.Signal = line[(line.IndexOf(':') + 1)..].Trim();
                    else if (line.StartsWith("Authentication")) cur.Auth = line[(line.IndexOf(':') + 1)..].Trim();
                    else if (line.StartsWith("Channel")) cur.Channel = line[(line.IndexOf(':') + 1)..].Trim();
                }
            }
            if (cur != null) list.Add(cur);
        }
        catch { }
        return list;
    }
}
