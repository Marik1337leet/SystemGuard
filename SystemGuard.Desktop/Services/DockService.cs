using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace SystemGuard.Desktop.Services;

public class DockApp
{
    public string Name { get; set; } = "";
    public string ExePath { get; set; } = "";
}

public class DockSettings
{
    public bool Enabled { get; set; }
    public bool AutoHide { get; set; }
    public bool Magnification { get; set; } = true;
    public int IconSize { get; set; } = 48; // 32..72
}

// ── Нативный macOS-style Dock внутри SystemGuard ────────────────────────────
// Панель поверх всех окон снизу по центру: закреплённые приложения,
// точки запущенных, запуск по клику, увеличение иконок. Без RocketDock/Nexus.
public class DockService
{
    private readonly string _dir;
    private readonly string _appsPath;
    private readonly string _settingsPath;

    public DockService()
    {
        _dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SystemGuard");
        Directory.CreateDirectory(_dir);
        _appsPath = Path.Combine(_dir, "dock.json");
        _settingsPath = Path.Combine(_dir, "dock-settings.json");
    }

    public List<DockApp> LoadApps()
    {
        try
        {
            if (!File.Exists(_appsPath))
                return DefaultApps().Where(a => File.Exists(a.ExePath)).ToList();
            return JsonSerializer.Deserialize<List<DockApp>>(File.ReadAllText(_appsPath)) ?? new();
        }
        catch { return new(); }
    }

    public void SaveApps(IEnumerable<DockApp> apps)
    {
        try { File.WriteAllText(_appsPath, JsonSerializer.Serialize(apps.ToList())); } catch { }
    }

    public DockSettings LoadSettings()
    {
        try
        {
            if (File.Exists(_settingsPath))
                return JsonSerializer.Deserialize<DockSettings>(File.ReadAllText(_settingsPath)) ?? new();
        }
        catch { }
        return new DockSettings();
    }

    public void SaveSettings(DockSettings s)
    {
        try { File.WriteAllText(_settingsPath, JsonSerializer.Serialize(s)); } catch { }
    }

    public static List<DockApp> DefaultApps()
    {
        var list = new List<DockApp>();
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var candidates = new (string Name, string Path)[]
        {
            ("Explorer", Path.Combine(win, "explorer.exe")),
            ("Edge", @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe"),
            ("Chrome", @"C:\Program Files\Google\Chrome\Application\chrome.exe"),
            ("Notepad", Path.Combine(win, "notepad.exe")),
            ("Calculator", Path.Combine(win, "System32\\calc.exe")),
            ("Terminal", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"Microsoft\WindowsApps\wt.exe")),
            ("Settings", Path.Combine(win, "System32\\control.exe")),
        };
        foreach (var (n, p) in candidates)
            list.Add(new DockApp { Name = n, ExePath = p });
        return list;
    }

    public static List<DockApp> RunningWindowedApps()
    {
        var list = new List<DockApp>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.MainWindowHandle == IntPtr.Zero) continue;
                if (string.IsNullOrWhiteSpace(p.MainWindowTitle)) continue;
                if (SelfProtection.IsProtectedProcess(p.ProcessName)) continue;
                var path = "";
                try { path = p.MainModule?.FileName ?? ""; } catch { }
                if (string.IsNullOrEmpty(path) || !seen.Add(path)) continue;
                list.Add(new DockApp { Name = p.ProcessName, ExePath = path });
            }
            catch { }
            finally { try { p.Dispose(); } catch { } }
        }
        return list.OrderBy(a => a.Name).ToList();
    }

    public static bool IsRunning(string exePath)
    {
        if (string.IsNullOrEmpty(exePath)) return false;
        var name = Path.GetFileNameWithoutExtension(exePath);
        try { return Process.GetProcessesByName(name).Length > 0; }
        catch { return false; }
    }

    public static void Launch(string exePath)
    {
        try { Process.Start(new ProcessStartInfo(exePath) { UseShellExecute = true }); } catch { }
    }

    // Иконка exe → PNG-байты для Avalonia Bitmap (кэш в памяти вызывателя)
    public static byte[]? ExtractIconPng(string exePath, int size = 48)
    {
        try
        {
            if (!File.Exists(exePath)) return null;
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
            if (icon == null) return null;
            using var bmp = icon.ToBitmap();
            using var scaled = new System.Drawing.Bitmap(bmp, new System.Drawing.Size(size, size));
            using var ms = new MemoryStream();
            scaled.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            return ms.ToArray();
        }
        catch { return null; }
    }
}
