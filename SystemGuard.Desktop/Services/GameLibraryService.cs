using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace SystemGuard.Desktop.Services;

public class GameEntry
{
    public string Name { get; set; } = "";
    public string ExePath { get; set; } = "";
    public string Source { get; set; } = ""; // Steam | Epic | StartMenu
}

// ── Библиотека игр: Steam + Epic + меню Пуск ────────────────────────────────
// Только чтение (реестр/манифесты/ярлыки). Импорт создаёт профиль Game Mode.
public static class GameLibraryService
{
    public static List<GameEntry> ScanAll()
    {
        var list = new List<GameEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string name, string exe, string src)
        {
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(exe)) return;
            if (!File.Exists(exe)) return;
            if (!seen.Add(exe)) return;
            list.Add(new GameEntry { Name = name.Trim(), ExePath = exe, Source = src });
        }

        foreach (var (n, e) in ScanSteam()) Add(n, e, "Steam");
        foreach (var (n, e) in ScanEpic()) Add(n, e, "Epic");
        foreach (var (n, e) in ScanStartMenuGames()) Add(n, e, "StartMenu");

        return list.OrderBy(g => g.Name).ToList();
    }

    // ── Steam: SteamPath из реестра + libraryfolders.vdf + appmanifest_*.acf ──
    private static List<(string Name, string Exe)> ScanSteam()
    {
        var found = new List<(string, string)>();
        string? steamPath = null;
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam")
                ?? Registry.LocalMachine.OpenSubKey(@"Software\WOW6432Node\Valve\Steam");
            steamPath = k?.GetValue("SteamPath")?.ToString();
        }
        catch { }
        if (string.IsNullOrEmpty(steamPath) || !Directory.Exists(steamPath)) return found;

        var libraries = new List<string> { Path.Combine(steamPath, "steamapps") };
        try
        {
            var vdf = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
            if (File.Exists(vdf))
                foreach (var line in File.ReadLines(vdf))
                    foreach (var p in line.Split('"'))
                    {
                        var c = p.Trim();
                        if (c.Length > 3 && (c[1] == ':' || c.StartsWith("\\\\")) && Directory.Exists(c))
                        {
                            var sa = Path.Combine(c, "steamapps");
                            if (Directory.Exists(sa) && !libraries.Contains(sa)) libraries.Add(sa);
                        }
                    }
        }
        catch { }

        foreach (var lib in libraries)
        {
            string[] manifests;
            try { manifests = Directory.GetFiles(lib, "appmanifest_*.acf"); }
            catch { continue; }
            foreach (var m in manifests)
            {
                string? name = null, dir = null;
                try
                {
                    foreach (var line in File.ReadLines(m))
                    {
                        var kv = line.Split('"', StringSplitOptions.RemoveEmptyEntries);
                        if (kv.Length < 2) continue;
                        var k2 = kv[0].Trim().ToLowerInvariant();
                        if (k2 == "name") name = kv[1].Trim();
                        else if (k2 == "installdir") dir = kv[1].Trim();
                    }
                }
                catch { continue; }
                if (name == null || dir == null) continue;
                var gameDir = Path.Combine(lib, "common", dir);
                if (!Directory.Exists(gameDir)) continue;
                // exe: самый большой .exe в корне папки игры (эвристика)
                try
                {
                    var exe = new DirectoryInfo(gameDir).GetFiles("*.exe", SearchOption.TopDirectoryOnly)
                        .Where(f => !f.Name.Contains("uninstall", StringComparison.OrdinalIgnoreCase)
                                 && !f.Name.Contains("setup", StringComparison.OrdinalIgnoreCase)
                                 && !f.Name.Contains("crash", StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(f => f.Length)
                        .FirstOrDefault();
                    if (exe != null) found.Add((name, exe.FullName));
                }
                catch { }
            }
        }
        return found;
    }

    // ── Epic: %ProgramData%\Epic\UnrealEngineLauncher\LauncherInstalled.dat ──
    private static List<(string Name, string Exe)> ScanEpic()
    {
        var found = new List<(string, string)>();
        var dat = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Epic", "UnrealEngineLauncher", "LauncherInstalled.dat");
        if (!File.Exists(dat)) return found;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(dat));
            if (!doc.RootElement.TryGetProperty("InstallationList", out var arr)) return found;
            foreach (var el in arr.EnumerateArray())
            {
                try
                {
                    var name = el.TryGetProperty("AppName", out var a) ? a.GetString() ?? "" : "";
                    var dir = el.TryGetProperty("InstallLocation", out var l) ? l.GetString() ?? "" : "";
                    if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
                    var exe = new DirectoryInfo(dir).GetFiles("*.exe", SearchOption.TopDirectoryOnly)
                        .Where(f => !f.Name.Contains("uninstall", StringComparison.OrdinalIgnoreCase)
                                 && !f.Name.Contains("setup", StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(f => f.Length)
                        .FirstOrDefault();
                    // Binaries подпапка — частый случай UE-игр
                    exe ??= new DirectoryInfo(dir).GetDirectories("Binaries", SearchOption.AllDirectories)
                        .SelectMany(d => { try { return d.GetFiles("*-Win64-Shipping.exe"); } catch { return Array.Empty<FileInfo>(); } })
                        .OrderByDescending(f => f.Length)
                        .FirstOrDefault();
                    if (exe != null) found.Add((string.IsNullOrEmpty(name) ? exe.Name : name, exe.FullName));
                }
                catch { }
            }
        }
        catch { }
        return found;
    }

    // ── Ярлыки игр из меню Пуск (все пользователи + текущий) ──
    private static IEnumerable<(string Name, string Exe)> ScanStartMenuGames()
    {
        var roots = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs")
        };
        string[] keys = { "game", "steam", "epic", "gog", "ubisoft", "ea ", "riot" };
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            string[] links;
            try { links = Directory.GetFiles(root, "*.lnk", SearchOption.AllDirectories); }
            catch { continue; }
            foreach (var lnk in links)
            {
                var n = Path.GetFileNameWithoutExtension(lnk);
                if (!keys.Any(k => n.Contains(k, StringComparison.OrdinalIgnoreCase))) continue;
                var target = ResolveShortcut(lnk);
                if (target != null) yield return (n, target);
            }
        }
    }

    private static string? ResolveShortcut(string lnk)
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return null;
            dynamic shell = Activator.CreateInstance(shellType)!;
            try
            {
                dynamic sc = shell.CreateShortcut(lnk);
                string target = sc.TargetPath;
                return File.Exists(target) && target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                    ? target : null;
            }
            finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
        }
        catch { return null; }
    }
}
