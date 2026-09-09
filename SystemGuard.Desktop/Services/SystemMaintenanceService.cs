using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace SystemGuard.Desktop.Services;

// Системное обслуживание Windows: точки восстановления, гибернация, UAC,
// BitLocker, обновления (WU COM API), драйверы, pagefile, разделы,
// планировщик Windows (schtasks), поиск невалидных ключей реестра.
// Всё best-effort: без прав администратора возвращается понятный текст.
public class RestorePointInfo
{
    public string Description { get; set; } = "";
    public DateTime Created { get; set; }
    public string Type { get; set; } = "";
}

public class InvalidRegistryEntry
{
    public string Hive { get; set; } = "";
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
    public string MissingPath { get; set; } = "";
}

public class DriverPackageInfo
{
    public string PublishedName { get; set; } = "";
    public string OriginalName { get; set; } = "";
    public string Version { get; set; } = "";
}

public static class SystemMaintenanceService
{
    private static string Run(string file, string args, int timeoutMs = 30000)
    {
        try
        {
            var psi = new ProcessStartInfo(file, args)
            {
                UseShellExecute = false, RedirectStandardOutput = true,
                RedirectStandardError = true, CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p == null) return "";
            var out1 = p.StandardOutput.ReadToEnd();
            p.WaitForExit(timeoutMs);
            return out1.Trim();
        }
        catch { return ""; }
    }

    // ── Точки восстановления ──────────────────────────────────────────────

    public static string CreateRestorePoint(string description = "SystemGuard manual point")
    {
        try
        {
            var safe = description.Replace("\"", "");
            var ps = $"Checkpoint-Computer -Description \"{safe}\" -RestorePointType MODIFY_SETTINGS";
            var psi = new ProcessStartInfo("powershell", $"-NoProfile -Command \"{ps}\"")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using var p = Process.Start(psi);
            p?.WaitForExit(120000);
            return p != null && p.ExitCode == 0 ? "Restore point created" : "Failed (run as admin, check System Protection is on)";
        }
        catch (Exception ex) { return $"Restore point failed: {ex.Message}"; }
    }

    public static List<RestorePointInfo> ListRestorePoints()
    {
        var list = new List<RestorePointInfo>();
        try
        {
            var ps = "Get-ComputerRestorePoint | Select-Object Description,CreationTime,RestorePointType | ConvertTo-Csv -NoTypeInformation";
            var csv = Run("powershell", $"-NoProfile -Command \"{ps}\"", 20000);
            foreach (var line in csv.Split('\n').Skip(1))
            {
                var parts = line.Trim().Trim('"').Split("\",\"");
                if (parts.Length < 2) continue;
                if (DateTime.TryParse(parts[1].Trim('"'), out var dt))
                    list.Add(new RestorePointInfo { Description = parts[0].Trim('"'), Created = dt, Type = parts.Length > 2 ? parts[2].Trim('"') : "" });
            }
        }
        catch { }
        return list.OrderByDescending(r => r.Created).ToList();
    }

    public static string DeleteOldSnapshots()
    {
        // Удаляет старые теневые копии, самую свежую оставляет (vssadmin).
        var out1 = Run("cmd.exe", "/c vssadmin delete shadows /for=C: /oldest /quiet");
        return string.IsNullOrWhiteSpace(out1) ? "Old shadow copy deleted (if any existed)" : out1;
    }

    public static string ListSnapshots() => Run("cmd.exe", "/c vssadmin list shadows");

    // ── Гибернация / pagefile ─────────────────────────────────────────────

    public static bool IsHibernateOn()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Power");
            var v = key?.GetValue("HibernateEnabled");
            return Convert.ToInt32(v ?? 1) != 0;
        }
        catch { return true; }
    }

    public static string SetHibernate(bool on)
    {
        var out1 = Run("powercfg", on ? "/hibernate on" : "/hibernate off");
        return string.IsNullOrWhiteSpace(out1)
            ? (on ? "Hibernation enabled" : "Hibernation disabled, hiberfil.sys removed")
            : out1;
    }

    public static string GetPagefileInfo()
    {
        try
        {
            using var s = new ManagementObjectSearcher("SELECT AllocatedBaseSize, CurrentUsage FROM Win32_PageFileUsage");
            var parts = new List<string>();
            foreach (ManagementObject o in s.Get())
                parts.Add($"allocated {o["AllocatedBaseSize"]}MB, in use {o["CurrentUsage"]}MB");
            using var cs = new ManagementObjectSearcher("SELECT AutomaticManagedPagefile FROM Win32_ComputerSystem");
            foreach (ManagementObject o in cs.Get())
                parts.Add("managed by Windows: " + o["AutomaticManagedPagefile"]);
            return parts.Count > 0 ? string.Join("; ", parts) : "No pagefile data";
        }
        catch (Exception ex) { return ex.Message; }
    }

    // ── UAC / BitLocker ───────────────────────────────────────────────────

    public static string GetUacLevel()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System");
            var lua = Convert.ToInt32(key?.GetValue("EnableLUA") ?? 1);
            var consent = Convert.ToInt32(key?.GetValue("ConsentPromptBehaviorAdmin") ?? 5);
            if (lua == 0) return "Off (not recommended)";
            return consent switch { 0 => "Level 1 (no dim)", 5 => "Level 3 (default)", 2 => "Level 4 (always notify)", _ => $"Level (consent={consent})" };
        }
        catch (Exception ex) { return ex.Message; }
    }

    public static string BitLockerStatus(string drive = "C:")
    {
        var out1 = Run("cmd.exe", $"/c manage-bde -status {drive}");
        if (string.IsNullOrWhiteSpace(out1)) return "BitLocker data unavailable (run as admin)";
        var lines = out1.Split('\n').Where(l => l.Contains("Conversion") || l.Contains("Protection") || l.Contains("Percentage")).Take(4);
        return string.Join(" | ", lines.Select(l => l.Trim()));
    }

    // ── Обновления Windows (WU COM API, без новых зависимостей) ──────────

    public static string GetPendingUpdates()
    {
        try
        {
            var t = Type.GetTypeFromProgID("Microsoft.Update.Session");
            if (t == null) return "WU API unavailable";
            dynamic session = Activator.CreateInstance(t)!;
            dynamic searcher = session.CreateUpdateSearcher();
            dynamic result = searcher.Search("IsInstalled=0 and IsHidden=0");
            int count = (int)result.Updates.Count;
            var titles = new List<string>();
            for (int i = 0; i < Math.Min(count, 10); i++)
                titles.Add((string)result.Updates.Item(i).Title);
            return count == 0 ? "No pending updates" : $"{count} pending: " + string.Join(" | ", titles.Select(Shorten));
        }
        catch (Exception ex) { return $"WU query failed: {ex.Message}"; }
    }

    private static string Shorten(string s) => s.Length > 80 ? s[..80] + "…" : s;

    public static string TriggerUpdateScan()
    {
        Run("usoclient", "StartScan");
        return "Update scan triggered (see Settings → Windows Update)";
    }

    // ── Драйверы / устройства ─────────────────────────────────────────────

    public static List<string> GetProblemDevices()
    {
        var list = new List<string>();
        try
        {
            using var s = new ManagementObjectSearcher("SELECT Name, ConfigManagerErrorCode FROM Win32_PnPEntity WHERE ConfigManagerErrorCode <> 0");
            foreach (ManagementObject o in s.Get())
                list.Add($"{o["Name"]} (code {o["ConfigManagerErrorCode"]})");
        }
        catch { }
        return list;
    }

    public static List<DriverPackageInfo> EnumDriverPackages()
    {
        var list = new List<DriverPackageInfo>();
        try
        {
            var out1 = Run("pnputil", "/enum-drivers");
            // Языконезависимый разбор: pnputil локализован (на RU метки русские),
            // поэтому делим вывод на блоки и ищем .inf + версию регуляркой.
            foreach (var block in out1.Split(new[] { "\r\n\r\n", "\n\n" }, StringSplitOptions.RemoveEmptyEntries))
            {
                var infs = new List<string>();
                string version = "";
                foreach (var raw in block.Split('\n'))
                {
                    var line = raw.Trim();
                    var ci = line.IndexOf(':');
                    var val = ci >= 0 ? line[(ci + 1)..].Trim() : line;
                    if (val.EndsWith(".inf", StringComparison.OrdinalIgnoreCase) && val.Length < 64)
                        infs.Add(val);
                    if (version == "")
                    {
                        var m = Regex.Match(val, @"\d+\.\d+\.\d[\d.]*");
                        if (m.Success) version = m.Value;
                    }
                }
                if (infs.Count > 0)
                    list.Add(new DriverPackageInfo
                    {
                        PublishedName = infs[0],
                        OriginalName = infs.Count > 1 && !infs[1].StartsWith("oem", StringComparison.OrdinalIgnoreCase) ? infs[1] : "",
                        Version = version
                    });
            }
        }
        catch { }
        return list;
    }

    public static void OpenDeviceManager()
    {
        try { Process.Start(new ProcessStartInfo("devmgmt.msc") { UseShellExecute = true }); } catch { }
    }

    // ── Разделы диска (только чтение + открытие оснастки) ────────────────

    public static List<string> ListPartitions()
    {
        var list = new List<string>();
        try
        {
            using var s = new ManagementObjectSearcher("SELECT DeviceID, Size, FileSystem FROM Win32_LogicalDisk WHERE DriveType=3");
            foreach (ManagementObject o in s.Get())
            {
                var bytes = Convert.ToUInt64(o["Size"] ?? 0);
                list.Add($"{o["DeviceID"]} {o["FileSystem"]} {bytes / 1073741824}GB");
            }
        }
        catch { }
        return list;
    }

    public static void OpenDiskManagement()
    {
        try { Process.Start(new ProcessStartInfo("diskmgmt.msc") { UseShellExecute = true }); } catch { }
    }

    // ── Планировщик Windows (schtasks, настоящий) ─────────────────────────

    public static List<string> ListWindowsTasks(string folder = "\\Microsoft\\Windows\\")
    {
        var list = new List<string>();
        try
        {
            var out1 = Run("schtasks", "/query /FO CSV /NH", 20000);
            foreach (var line in out1.Split('\n').Take(300))
            {
                var t = line.Trim().Trim('"');
                if (!string.IsNullOrWhiteSpace(t)) list.Add(t.Split("\",\"").First().Trim('"'));
            }
        }
        catch { }
        return list.Where(t => t.StartsWith(folder) || folder == "\\").Take(200).ToList();
    }

    public static string CreateWindowsTask(string name, string command, string schedule = "DAILY", string time = "09:00")
    {
        var out1 = Run("schtasks", $"/create /TN \"{name}\" /TR \"{command}\" /SC {schedule} /ST {time} /F");
        return string.IsNullOrWhiteSpace(out1) ? $"Task '{name}' created" : out1.Trim();
    }

    public static string DeleteWindowsTask(string name)
    {
        var out1 = Run("schtasks", $"/delete /TN \"{name}\" /F");
        return string.IsNullOrWhiteSpace(out1) ? $"Task '{name}' deleted" : out1.Trim();
    }

    // ── Невалидные ключи реестра (только поиск, удаление — явное) ────────

    public static List<InvalidRegistryEntry> FindInvalidKeys()
    {
        var bad = new List<InvalidRegistryEntry>();
        void CheckValue(string hive, RegistryKey root, string subKey, string valueName, string? target)
        {
            if (string.IsNullOrWhiteSpace(target)) return;
            var path = target.Trim().Trim('"');
            // Отрезаем аргументы: "C:\a\b.exe /x" → C:\a\b.exe
            if (path.StartsWith("\""))
            {
                var end = path.IndexOf('"', 1);
                if (end > 1) path = path[1..end];
            }
            else
            {
                var exe = path.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
                if (exe > 0) path = path[..(exe + 4)];
            }
            path = Environment.ExpandEnvironmentVariables(path);
            if (!path.Contains(':') && !path.StartsWith(@"\\")) return; // команда без пути
            try { if (!File.Exists(path) && !Directory.Exists(path)) bad.Add(new InvalidRegistryEntry { Hive = hive, Key = subKey, Value = valueName, MissingPath = path }); }
            catch { }
        }

        foreach (var (hive, root) in new[] { ("HKCU", Registry.CurrentUser), ("HKLM", Registry.LocalMachine) })
        {
            foreach (var run in new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce" })
            {
                try
                {
                    using var key = root.OpenSubKey(run);
                    if (key == null) continue;
                    foreach (var v in key.GetValueNames())
                        CheckValue(hive, root, run, v, key.GetValue(v)?.ToString());
                }
                catch { }
            }
            try
            {
                using var ap = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths");
                if (ap != null) foreach (var sub in ap.GetSubKeyNames().Take(200))
                {
                    try
                    {
                        using var k = ap.OpenSubKey(sub);
                        CheckValue(hive, root, $"App Paths\\{sub}", "(Default)", k?.GetValue(null)?.ToString());
                    }
                    catch { }
                }
            }
            catch { }
        }
        try
        {
            using var shared = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\SharedDLLs");
            if (shared != null) foreach (var v in shared.GetValueNames().Take(500))
                if (!File.Exists(v)) bad.Add(new InvalidRegistryEntry { Hive = "HKLM", Key = @"…\SharedDLLs", Value = v, MissingPath = v });
        }
        catch { }
        return bad.Take(300).ToList();
    }

    public static string RemoveRunValue(string hive, string valueName)
    {
        try
        {
            var root = hive == "HKLM" ? Registry.LocalMachine : Registry.CurrentUser;
            using var key = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
            key?.DeleteValue(valueName, false);
            return $"Removed {hive}\\…\\Run → {valueName}";
        }
        catch (Exception ex) { return $"Remove failed: {ex.Message}"; }
    }
}
