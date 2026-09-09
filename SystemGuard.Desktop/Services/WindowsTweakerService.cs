using System;
using System.Diagnostics;
using Microsoft.Win32;

namespace SystemGuard.Desktop.Services;

// Твикер Windows: телеметрия, UAC, BitLocker-статус. Изменения обратимы, с сообщениями.
public static class WindowsTweakerService
{
    public static string GetTelemetry()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Policies\Microsoft\Windows\DataCollection");
            var v = key?.GetValue("AllowTelemetry");
            return v == null ? "Not configured (default)" : $"AllowTelemetry = {v}";
        }
        catch (Exception ex) { return ex.Message; }
    }

    public static string SetTelemetry(bool disable)
    {
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Policies\Microsoft\Windows\DataCollection");
            if (key == null) return "Cannot open policy key (run as admin)";
            key.SetValue("AllowTelemetry", disable ? 0 : 1, RegistryValueKind.DWord);
            return disable ? "Telemetry disabled" : "Telemetry enabled";
        }
        catch (Exception ex) { return $"Telemetry failed: {ex.Message} (run as admin)"; }
    }

    public static string GetUac()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System");
            return $"EnableLUA = {key?.GetValue("EnableLUA")}, ConsentPrompt = {key?.GetValue("ConsentPromptBehaviorAdmin")}";
        }
        catch (Exception ex) { return ex.Message; }
    }

    public static string BitLockerStatus()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "manage-bde", Arguments = "-status C:",
                UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p == null) return "Cannot query BitLocker";
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(15000);
            foreach (var line in output.Split('\n'))
                if (line.Contains("Conversion Status") || line.Contains("Protection Status"))
                    return line.Trim();
            return "BitLocker: no data";
        }
        catch (Exception ex) { return $"BitLocker query failed: {ex.Message}"; }
    }
}
