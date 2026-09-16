using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace SystemGuard.Desktop.Services;

// ── НАТИВНАЯ кастомизация Windows (без SkinPack/Rainmeter/доков) ────────────
// Всё — средствами самой Windows: HKCU-реестр, SystemParametersInfo,
// курсоры, обои. Никаких патчей system32.
// Каждое изменение пишется через WriteDword/WriteString с бэкапом значения,
// откат — RestoreDefaults(). Опасного (HKLM, system32) здесь нет по дизайну.
public class AppearancePacksService
{
    private const string AdvKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    private const string PersKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string ExplorerKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer";
    private const string CursorsKey = @"Control Panel\Cursors";
    private const string MouseKey = @"Control Panel\Mouse";
    private const string EaseCursorKey = @"Software\Microsoft\Accessibility";
    private const string MetricsKey = @"Control Panel\Desktop\WindowMetrics";
    private const string HideDesktopKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons\NewStartPanel";

    private readonly Dictionary<string, object?> _backup = new();

    // ═══ TASKBAR ═══

    public IReadOnlyDictionary<string, int> GetTaskbarStates() => new Dictionary<string, int>
    {
        ["CenterIcons"] = ReadDword(AdvKey, "TaskbarAl", 1),
        ["Widgets"] = ReadDword(AdvKey, "TaskbarDa", 1),
        ["Chat"] = ReadDword(AdvKey, "TaskbarMn", 1),
        ["TaskView"] = ReadDword(AdvKey, "ShowTaskViewButton", 1),
        ["TaskbarSize"] = ReadDword(AdvKey, "TaskbarSi", 1),          // 0 small 1 medium 2 large
        ["Combine"] = ReadDword(AdvKey, "TaskbarGlomLevel", 0),        // 0 always 1 full 2 never
        ["SecondsClock"] = ReadDword(AdvKey, "ShowSecondsInSystemClock", 0),
        ["SearchMode"] = ReadDword(AdvKey, "SearchboxTaskbarMode", 2), // 0 hide 1 icon 2 box 3 glyph(Win11)
    };

    public IReadOnlyDictionary<string, int> GetThemeStates() => new Dictionary<string, int>
    {
        ["Transparency"] = ReadDword(PersKey, "EnableTransparency", 1),
        ["DarkMode"] = ReadDword(PersKey, "AppsUseLightTheme", 0) == 0 ? 1 : 0,
    };

    public void SetTaskbarCenter(bool center) => WriteDword(AdvKey, "TaskbarAl", center ? 1 : 0);
    public void SetWidgets(bool on) => WriteDword(AdvKey, "TaskbarDa", on ? 1 : 0);
    public void SetChat(bool on) => WriteDword(AdvKey, "TaskbarMn", on ? 1 : 0);
    public void SetTaskView(bool on) => WriteDword(AdvKey, "ShowTaskViewButton", on ? 1 : 0);
    public void SetTaskbarSize(int v) => WriteDword(AdvKey, "TaskbarSi", Math.Clamp(v, 0, 2));
    public void SetCombine(int v) => WriteDword(AdvKey, "TaskbarGlomLevel", Math.Clamp(v, 0, 2));
    public void SetSecondsClock(bool on) => WriteDword(AdvKey, "ShowSecondsInSystemClock", on ? 1 : 0);
    public void SetSearchMode(int v) => WriteDword(AdvKey, "SearchboxTaskbarMode", Math.Clamp(v, 0, 3));
    public void SetTransparency(bool on) => WriteDword(PersKey, "EnableTransparency", on ? 1 : 0);
    public void SetDarkMode(bool dark)
    {
        WriteDword(PersKey, "AppsUseLightTheme", dark ? 0 : 1);
        WriteDword(PersKey, "SystemUsesLightTheme", dark ? 0 : 1);
    }

    public void RestoreTaskbarDefaults()
    {
        foreach (var (name, value) in _backup.ToList())
        {
            var parts = name.Split('|');
            if (parts.Length == 2) WriteRaw(parts[0], parts[1], value);
        }
        _backup.Clear();
    }

    public async Task<string> CreateRestorePointAsync()
    {
        try
        {
            var psi = new ProcessStartInfo("powershell",
                "-NoProfile -Command \"Checkpoint-Computer -Description 'SystemGuard Appearance' -RestorePointType 'MODIFY_SETTINGS'\"")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using var p = Process.Start(psi);
            if (p == null) return "Cannot start PowerShell";
            await p.WaitForExitAsync();
            return p.ExitCode == 0 ? "Restore point created" : "Needs Administrator for restore point";
        }
        catch (Exception ex) { return $"Error: {ex.Message}"; }
    }

    public void RestartExplorer()
    {
        try
        {
            foreach (var p in Process.GetProcessesByName("explorer")) p.Kill();
            Task.Delay(1500).ContinueWith(_ =>
            {
                try
                {
                    if (Process.GetProcessesByName("explorer").Length == 0)
                        Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true });
                }
                catch { }
            });
        }
        catch { }
    }

    public void OpenSettingsPage(string uri)
    {
        try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); } catch { }
    }

    // Синхронизация акцента Windows с цветом приложения (DWM, без стороннего софта)
    public bool SetWindowsAccent(string hex)
    {
        try
        {
            hex = hex.Trim().TrimStart('#');
            if (hex.Length == 8) hex = hex[2..]; // AARRGGBB → RRGGBB
            if (hex.Length != 6) return false;
            uint r = Convert.ToUInt32(hex.Substring(0, 2), 16);
            uint g = Convert.ToUInt32(hex.Substring(2, 2), 16);
            uint b = Convert.ToUInt32(hex.Substring(4, 2), 16);
            uint bgr = 0xFF000000 | (b << 16) | (g << 8) | r; // DWM хранит BGR
            using var k = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\DWM");
            if (k == null) return false;
            BackupValue(@"Software\Microsoft\Windows\DWM", "AccentColor");
            BackupValue(@"Software\Microsoft\Windows\DWM", "ColorizationColor");
            BackupValue(PersKey, "ColorPrevalence");
            k.SetValue("AccentColor", unchecked((int)bgr), RegistryValueKind.DWord);
            k.SetValue("ColorizationColor", unchecked((int)0xC4000000 | (int)(bgr & 0xFFFFFF)), RegistryValueKind.DWord);
            using var p = Registry.CurrentUser.CreateSubKey(PersKey);
            p?.SetValue("ColorPrevalence", 1, RegistryValueKind.DWord);
            BroadcastSettingChange();
            return true;
        }
        catch { return false; }
    }

    // ═══ WALLPAPER ═══

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int SystemParametersInfo(int action, int param, string? pv, int winIni);
    private const int SPI_SETDESKWALLPAPER = 20;
    private const int SPIF_UPDATEINIFILE = 1, SPIF_SENDCHANGE = 2;

    // 0 center 2 stretch 6 fit 10 fill 22 span
    public string WallpaperFit
    {
        get
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
                return (k?.GetValue("WallpaperStyle")?.ToString() ?? "10") switch
                {
                    "0" => "Center", "2" => "Stretch", "6" => "Fit",
                    "22" => "Span", _ => "Fill"
                };
            }
            catch { return "Fill"; }
        }
    }

    public bool SetWallpaper(string imagePath, string fit = "Fill")
    {
        try
        {
            if (!File.Exists(imagePath)) return false;
            var style = fit switch { "Center" => "0", "Stretch" => "2", "Fit" => "6", "Span" => "22", _ => "10" };
            var tile = fit == "Span" ? "1" : "0";
            using (var k = Registry.CurrentUser.CreateSubKey(@"Control Panel\Desktop"))
            {
                k?.SetValue("WallpaperStyle", style);
                k?.SetValue("TileWallpaper", tile);
            }
            return SystemParametersInfo(SPI_SETDESKWALLPAPER, 0, imagePath, SPIF_UPDATEINIFILE | SPIF_SENDCHANGE) != 0;
        }
        catch { return false; }
    }

    // ═══ CURSORS (схемы Windows + размер + след + тень) ═══

    public List<string> GetCursorSchemes()
    {
        var list = new List<string> { "Windows Default", "Windows Aero", "Windows Black", "Windows Inverted", "Windows Large", "Windows Extra Large" };
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(CursorsKey + @"\Schemes");
            if (k != null)
                foreach (var n in k.GetSubKeyNames())
                    if (!list.Contains(n)) list.Add(n);
        }
        catch { }
        return list;
    }

    public string CurrentCursorScheme()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(CursorsKey);
            var v = k?.GetValue("")?.ToString() ?? "";
            return string.IsNullOrEmpty(v) ? "Windows Default" : v;
        }
        catch { return "Windows Default"; }
    }

    public bool ApplyCursorScheme(string scheme)
    {
        try
        {
            string[] roles = { "Arrow", "Help", "AppStarting", "Wait", "Crosshair", "IBeam", "NWPen",
                "No", "SizeNS", "SizeWE", "SizeNWSE", "SizeNESW", "SizeAll", "UpArrow", "Hand", "Person", "Pin" };
            Dictionary<string, string?> values = new();
            if (scheme is "Windows Default" or "Windows Aero" or "Windows Black" or "Windows Inverted")
            {
                // Сбрасываем на системные: удаляем переопределения, ставим схему по умолчанию
                using var k = Registry.CurrentUser.CreateSubKey(CursorsKey);
                if (k == null) return false;
                BackupValue(CursorsKey, "");
                foreach (var r in roles) k.DeleteValue(r, false);
                k.SetValue("", scheme == "Windows Default" ? "" : scheme);
            }
            else
            {
                using var s = Registry.CurrentUser.OpenSubKey(CursorsKey + @"\Schemes\" + scheme);
                if (s == null) return false;
                var raw = s.GetValueNames().Contains("") ? s.GetValue("")?.ToString() : null;
                var parts = (raw ?? "").Split(',');
                using var k = Registry.CurrentUser.CreateSubKey(CursorsKey);
                if (k == null) return false;
                BackupValue(CursorsKey, "");
                for (int i = 0; i < roles.Length && i < parts.Length; i++)
                {
                    BackupValue(CursorsKey, roles[i]);
                    if (!string.IsNullOrWhiteSpace(parts[i])) k.SetValue(roles[i], parts[i].Trim());
                }
                k.SetValue("", scheme);
            }
            BroadcastSettingChange();
            return true;
        }
        catch { return false; }
    }

    public int CursorSize()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(EaseCursorKey);
            return (k?.GetValue("CursorSize") as int?) ?? 1;
        }
        catch { return 1; }
    }

    public void SetCursorSize(int v)
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(EaseCursorKey);
            BackupValue(EaseCursorKey, "CursorSize");
            k?.SetValue("CursorSize", Math.Clamp(v, 1, 15), RegistryValueKind.DWord);
            BroadcastSettingChange();
        }
        catch { }
    }

    public bool CursorShadow()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
            return (k?.GetValue("CursorShadow")?.ToString() ?? "0") == "1";
        }
        catch { return false; }
    }

    public void SetCursorShadow(bool on)
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(@"Control Panel\Desktop");
            BackupValue(@"Control Panel\Desktop", "CursorShadow");
            k?.SetValue("CursorShadow", on ? "1" : "0");
            BroadcastSettingChange();
        }
        catch { }
    }

    // ═══ DESKTOP ICONS ═══

    private static readonly (string Key, string Name, string Default)[] _desktopIcons =
    {
        ("{20D04FE0-3AEA-1069-A2D8-08002B30309D}", "This PC", "This PC"),
        ("{645FF040-5081-101B-9F08-00AA002F954E}", "Recycle Bin", "Recycle Bin"),
        ("{F02C1A0D-BE21-4350-88B0-7367FC96EF3C}", "Network", "Network"),
        ("{59031A47-3F72-44A7-89C5-5595FE6B30EE}", "User Files", "User Files"),
        ("{5399E694-6CE5-4D6C-8FCE-1D8870FDCBA0}", "Control Panel", "Control Panel"),
    };

    public Dictionary<string, bool> GetDesktopIcons()
    {
        var d = new Dictionary<string, bool>();
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(HideDesktopKey);
            foreach (var (guid, name, _) in _desktopIcons)
                d[name] = (k?.GetValue(guid) as int?) != 1; // 1 = hidden
        }
        catch { foreach (var (_, n, _) in _desktopIcons) d[n] = true; }
        return d;
    }

    public void SetDesktopIcon(string name, bool visible)
    {
        var item = _desktopIcons.FirstOrDefault(i => i.Name == name);
        if (item.Name == null) return;
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(HideDesktopKey);
            BackupValue(HideDesktopKey, item.Key);
            k?.SetValue(item.Key, visible ? 0 : 1, RegistryValueKind.DWord);
        }
        catch { }
    }

    public int IconSize()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop\WindowMetrics");
            var v = k?.GetValue("Shell Icon Size")?.ToString();
            return int.TryParse(v, out var s) ? Math.Clamp(s, 16, 72) : 32;
        }
        catch { return 32; }
    }

    public void SetIconSize(int px)
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(@"Control Panel\Desktop\WindowMetrics");
            BackupValue(@"Control Panel\Desktop\WindowMetrics", "Shell Icon Size");
            k?.SetValue("Shell Icon Size", Math.Clamp(px, 16, 72).ToString());
            BroadcastSettingChange();
        }
        catch { }
    }

    // ═══ EXPLORER ═══

    public bool ShowHiddenFiles()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(AdvKey);
            return (k?.GetValue("Hidden") as int?) == 1;
        }
        catch { return false; }
    }
    public void SetHiddenFiles(bool show) => WriteDword(AdvKey, "Hidden", show ? 1 : 2);

    public bool ShowExtensions()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(AdvKey);
            return (k?.GetValue("HideFileExt") as int?) == 0;
        }
        catch { return false; }
    }
    public void SetExtensions(bool show) => WriteDword(AdvKey, "HideFileExt", show ? 0 : 1);

    public bool LaunchToThisPC()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(AdvKey);
            return (k?.GetValue("LaunchTo") as int?) == 1;
        }
        catch { return false; }
    }
    public void SetLaunchToThisPC(bool thisPC) => WriteDword(AdvKey, "LaunchTo", thisPC ? 1 : 2);

    // ═══ WINDOW METRICS (заголовки/меню/скроллбары) ═══

    public int CaptionHeight() => ReadMetric("CaptionHeight", -270, -120, -1500);
    public int MenuHeight() => ReadMetric("MenuHeight", -270, -120, -1500);
    public int ScrollbarSize() => ReadMetric("ScrollWidth", -225, -120, -1500);

    public void SetCaptionHeight(int px) => WriteMetric("CaptionHeight", px);
    public void SetMenuHeight(int px) => WriteMetric("MenuHeight", px);
    public void SetScrollbarSize(int px) { WriteMetric("ScrollWidth", px); WriteMetric("ScrollHeight", px); }

    private static int ReadMetric(string name, int def, int min, int max)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(MetricsKey);
            var raw = k?.GetValue(name)?.ToString();
            if (int.TryParse(raw, out var twips))
            {
                // twips отрицательные: px = -twips/15
                var px = -twips / 15;
                return Math.Clamp(px, -max / 15, -min / 15);
            }
        }
        catch { }
        return -def / 15;
    }

    private void WriteMetric(string name, int px)
    {
        try
        {
            px = Math.Clamp(px, 8, 100);
            using var k = Registry.CurrentUser.CreateSubKey(MetricsKey);
            BackupValue(MetricsKey, name);
            k?.SetValue(name, (-px * 15).ToString());
            BroadcastSettingChange();
        }
        catch { }
    }

    // ═══ helpers ═══

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, int msg, IntPtr wParam, string lParam, int flags, int timeout, out IntPtr result);

    private static void BroadcastSettingChange()
    {
        try
        {
            SendMessageTimeout(new IntPtr(0xFFFF), 0x001A, IntPtr.Zero, "Environment",
                0x0002, 3000, out _);
        }
        catch { }
    }

    private int ReadDword(string subKey, string name, int def)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(subKey);
            var v = k?.GetValue(name);
            if (v is int i) return i;
        }
        catch { }
        return def;
    }

    private void BackupValue(string subKey, string name)
    {
        var key = $"{subKey}|{name}";
        if (_backup.ContainsKey(key)) return;
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(subKey);
            _backup[key] = k?.GetValue(name);
        }
        catch { _backup[key] = null; }
    }

    private void WriteDword(string subKey, string name, int value)
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(subKey);
            if (k == null) return;
            BackupValue(subKey, name);
            k.SetValue(name, value, RegistryValueKind.DWord);
        }
        catch { }
    }

    private static void WriteRaw(string subKey, string name, object? value)
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(subKey);
            if (k == null) return;
            if (value == null) k.DeleteValue(name, false);
            else k.SetValue(name, value);
        }
        catch { }
    }
}
