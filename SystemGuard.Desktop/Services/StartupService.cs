using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace SystemGuard.Desktop.Services;

public class StartupItem
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string Location { get; set; } = "";
    public bool IsEnabled { get; set; }
    public string Publisher { get; set; } = "";
    public string Description { get; set; } = "";
    public bool IsSigned { get; set; }
    public StartupImpact Impact { get; set; } = StartupImpact.Unknown;
}

public enum StartupImpact
{
    Unknown, None, Low, Medium, High
}

public class StartupService
{
    public List<StartupItem> GetStartupItems()
    {
        var items = new List<StartupItem>();

        // HKCU Run
        items.AddRange(GetRegistryStartupItems(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Run", "HKCU Run"));

        // HKLM Run
        items.AddRange(GetRegistryStartupItems(Registry.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\Run", "HKLM Run"));

        // HKCU RunOnce
        items.AddRange(GetRegistryStartupItems(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\RunOnce", "HKCU RunOnce"));

        // Startup folders
        items.AddRange(GetStartupFolderItems(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "User Startup"));
        items.AddRange(GetStartupFolderItems(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), "Common Startup"));

        // Scheduled tasks (simplified)
        items.AddRange(GetScheduledTasks());

        return items.OrderBy(i => i.Location).ThenBy(i => i.Name).ToList();
    }

    private List<StartupItem> GetRegistryStartupItems(RegistryKey hive, string subKey, string location)
    {
        var items = new List<StartupItem>();
        try
        {
            using var key = hive.OpenSubKey(subKey);
            if (key == null) return items;

            foreach (var valueName in key.GetValueNames())
            {
                var value = key.GetValue(valueName)?.ToString() ?? "";
                var item = new StartupItem
                {
                    Name = valueName,
                    Path = value,
                    Location = location,
                    IsEnabled = true
                };

                // Get file info
                if (!string.IsNullOrEmpty(value))
                {
                    var exePath = ExtractExePath(value);
                    if (File.Exists(exePath))
                    {
                        try
                        {
                            var fileInfo = FileVersionInfo.GetVersionInfo(exePath);
                            item.Publisher = fileInfo.CompanyName ?? "";
                            item.Description = fileInfo.FileDescription ?? "";
                            item.IsSigned = IsFileSigned(exePath);
                        }
                        catch { }
                    }
                }

                items.Add(item);
            }
        }
        catch { }
        return items;
    }

    private List<StartupItem> GetStartupFolderItems(string folderPath, string location)
    {
        var items = new List<StartupItem>();
        try
        {
            if (!Directory.Exists(folderPath)) return items;

            foreach (var file in Directory.GetFiles(folderPath, "*.lnk"))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                items.Add(new StartupItem
                {
                    Name = name,
                    Path = file,
                    Location = location,
                    IsEnabled = true
                });
            }
        }
        catch { }
        return items;
    }

    private List<StartupItem> GetScheduledTasks()
    {
        var items = new List<StartupItem>();
        try
        {
            var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "schtasks",
                    Arguments = "/query /fo CSV /NH",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                }
            };
            process.Start();
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();

            foreach (var line in output.Split('\n').Take(50))
            {
                var parts = line.Split(',');
                // schtasks локализован: EN "At log on"/"Enabled", RU "При входе"/"Готов"/"Выполняется"
                bool isLogon = line.Contains("At log on", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("При входе", StringComparison.OrdinalIgnoreCase);
                if (parts.Length >= 3 && isLogon)
                {
                    bool enabled = line.Contains("Enabled", StringComparison.OrdinalIgnoreCase)
                        || line.Contains("Готов", StringComparison.OrdinalIgnoreCase)
                        || line.Contains("Выполняется", StringComparison.OrdinalIgnoreCase);
                    items.Add(new StartupItem
                    {
                        Name = parts[0].Trim('"'),
                        Path = parts.Length > 2 ? parts[2].Trim('"') : "",
                        Location = "Scheduled Task",
                        IsEnabled = enabled
                    });
                }
            }
        }
        catch { }
        return items;
    }

    // Location вида "HKCU Run" / "HKLM RunOnce" — полный путь восстанавливаем маппингом.
    // (Раньше Replace("HKCU ", "") давал просто "Run" — ключ открывался неверный
    // и отключение автозапуска молча ничего не делало.)
    private static (Microsoft.Win32.RegistryKey Hive, string SubKey)? ResolveRunKey(string location)
    {
        string? sub = location.Contains("RunOnce") ? @"Software\Microsoft\Windows\CurrentVersion\RunOnce"
            : location.Contains("Run") ? @"Software\Microsoft\Windows\CurrentVersion\Run"
            : null;
        if (sub == null) return null;
        if (location.Contains("HKCU")) return (Microsoft.Win32.Registry.CurrentUser, sub);
        if (location.Contains("HKLM")) return (Microsoft.Win32.Registry.LocalMachine, sub);
        return null;
    }

    public void DisableStartupItem(StartupItem item)
    {
        try
        {
            var run = ResolveRunKey(item.Location);
            if (run.HasValue)
            {
                using var key = run.Value.Hive.OpenSubKey(run.Value.SubKey, true);
                key?.DeleteValue(item.Name, false);
            }
            else if (item.Location.Contains("Startup"))
            {
                var file = Path.Combine(
                    item.Location.Contains("Common") ?
                        Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup) :
                        Environment.GetFolderPath(Environment.SpecialFolder.Startup),
                    item.Name + ".lnk");
                if (File.Exists(file)) File.Delete(file);
            }
        }
        catch { }
    }

    public void EnableStartupItem(StartupItem item)
    {
        try
        {
            var run = ResolveRunKey(item.Location);
            if (run.HasValue)
            {
                using var key = run.Value.Hive.OpenSubKey(run.Value.SubKey, true);
                key?.SetValue(item.Name, item.Path);
            }
        }
        catch { }
    }

    private string ExtractExePath(string value)
    {
        value = value.Trim('"');
        var spaceIndex = value.IndexOf(' ');
        if (spaceIndex > 0 && value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return value;
        if (spaceIndex > 0)
            return value[..spaceIndex];
        return value;
    }

    private bool IsFileSigned(string path)
    {
        try
        {
            var fileInfo = FileVersionInfo.GetVersionInfo(path);
            return !string.IsNullOrEmpty(fileInfo.CompanyName);
        }
        catch { return false; }
    }

    public long GetStartupImpact()
    {
        var items = GetStartupItems().Where(i => i.IsEnabled).ToList();
        long totalSize = 0;
        foreach (var item in items)
        {
            try
            {
                var exePath = ExtractExePath(item.Path);
                if (File.Exists(exePath))
                    totalSize += new FileInfo(exePath).Length;
            }
            catch { }
        }
        return totalSize;
    }
}