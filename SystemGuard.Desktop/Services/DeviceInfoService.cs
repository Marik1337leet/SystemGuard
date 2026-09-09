using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Management;

namespace SystemGuard.Desktop.Services;

public class DeviceEntry
{
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Status { get; set; } = "";
}

// Устройства и драйверы (WMI, read-only) + открытие диспетчера устройств.
public static class DeviceInfoService
{
    public static List<DeviceEntry> ListDevices()
    {
        var list = new List<DeviceEntry>();
        try
        {
            if (!OperatingSystem.IsWindows()) return list;
            using var searcher = new ManagementObjectSearcher("SELECT Name, Status FROM Win32_PnPEntity");
            foreach (ManagementObject o in searcher.Get())
            {
                try
                {
                    list.Add(new DeviceEntry
                    {
                        Name = o["Name"]?.ToString() ?? "Unknown",
                        Kind = "PnP",
                        Status = o["Status"]?.ToString() ?? ""
                    });
                    if (list.Count >= 200) break;
                }
                catch { }
            }
        }
        catch { }
        return list;
    }

    public static string OpenDeviceManager()
    {
        try { Process.Start(new ProcessStartInfo("devmgmt.msc") { UseShellExecute = true }); return "Device Manager opened"; }
        catch (Exception ex) { return ex.Message; }
    }

    public static string OpenDisplaySettings()
    {
        try { Process.Start(new ProcessStartInfo("ms-settings:display") { UseShellExecute = true }); return "Display settings opened (HDR, refresh rate)"; }
        catch (Exception ex) { return ex.Message; }
    }
}
