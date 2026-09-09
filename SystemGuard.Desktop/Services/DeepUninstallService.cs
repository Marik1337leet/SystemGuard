using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Threading;
using System.Threading.Tasks;

namespace SystemGuard.Desktop.Services;

public partial class UninstallTrace : ObservableObject
{
    public string Type { get; set; } = "";      // "Registry", "File", "Folder", "Service", "Startup", "Shortcut"
    public string Path { get; set; } = "";
    public string Description { get; set; } = "";
    public long SizeBytes { get; set; }
    [ObservableProperty] private bool _isSelected = true;
    public bool IsDangerous { get; set; }       // системные пути — требует подтверждения
}

public class UninstallResult
{
    public int RemovedCount { get; set; }
    public long FreedBytes { get; set; }
    public List<string> Errors { get; set; } = new();
    public List<string> Removed { get; set; } = new();
}

public class InstalledProgram
{
    public string Name { get; set; } = "";
    public string Publisher { get; set; } = "";
    public string Version { get; set; } = "";
    public string InstallLocation { get; set; } = "";
    public string UninstallString { get; set; } = "";
    public string RegistryKey { get; set; } = "";
    public long EstimatedSizeMb { get; set; }
    public DateTime? InstallDate { get; set; }
}

public class DeepUninstallService
{
    private static readonly string[] _safeSkipPaths = new[]
    {
        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        Environment.GetFolderPath(Environment.SpecialFolder.System),
        Environment.GetFolderPath(Environment.SpecialFolder.SystemX86),
    };

    // ── Получить список установленных программ ────────────────────────────────

    public async Task<List<InstalledProgram>> GetInstalledProgramsAsync()
    {
        return await Task.Run(() =>
        {
            var programs = new List<InstalledProgram>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // HKLM 64-bit
            ScanUninstallKey(
                Registry.LocalMachine,
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                programs, seen);

            // HKLM 32-bit on 64-bit OS
            ScanUninstallKey(
                Registry.LocalMachine,
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
                programs, seen);

            // HKCU (per-user installs)
            ScanUninstallKey(
                Registry.CurrentUser,
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                programs, seen);

            return programs
                .Where(p => !SelfProtection.IsProtectedProgram(p.Name, p.InstallLocation))
                .OrderBy(p => p.Name)
                .ToList();
        });
    }

    private static void ScanUninstallKey(RegistryKey hive, string subKeyPath,
        List<InstalledProgram> programs, HashSet<string> seen)
    {
        try
        {
            using var key = hive.OpenSubKey(subKeyPath);
            if (key == null) return;

            foreach (var subName in key.GetSubKeyNames())
            {
                try
                {
                    using var sub = key.OpenSubKey(subName);
                    if (sub == null) continue;

                    var name = sub.GetValue("DisplayName")?.ToString();
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    if (!seen.Add(name)) continue;

                    // Пропускаем системные обновления и компоненты
                    var isSystemComponent = sub.GetValue("SystemComponent") as int? == 1;
                    var isUpdate = name.StartsWith("Security Update") ||
                                   name.StartsWith("Update for") ||
                                   name.StartsWith("KB") ||
                                   name.Contains("Hotfix");
                    if (isSystemComponent || isUpdate) continue;

                    var installDate = DateTime.MinValue;
                    var dateStr = sub.GetValue("InstallDate")?.ToString();
                    if (!string.IsNullOrEmpty(dateStr) && dateStr.Length == 8)
                        DateTime.TryParseExact(dateStr, "yyyyMMdd",
                            null, System.Globalization.DateTimeStyles.None, out installDate);

                    programs.Add(new InstalledProgram
                    {
                        Name = name,
                        Publisher = sub.GetValue("Publisher")?.ToString() ?? "",
                        Version = sub.GetValue("DisplayVersion")?.ToString() ?? "",
                        InstallLocation = sub.GetValue("InstallLocation")?.ToString() ?? "",
                        UninstallString = sub.GetValue("UninstallString")?.ToString() ?? "",
                        RegistryKey = $"{hive.Name}\\{subKeyPath}\\{subName}",
                        EstimatedSizeMb = (sub.GetValue("EstimatedSize") as int? ?? 0) / 1024,
                        InstallDate = installDate == DateTime.MinValue ? null : installDate
                    });
                }
                catch { }
            }
        }
        catch { }
    }

    // ── Найти все следы программы ─────────────────────────────────────────────

    public async Task<List<UninstallTrace>> FindTracesAsync(
        InstalledProgram program,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var traces = new List<UninstallTrace>();

        // Ключевые слова для поиска — имя программы и издатель, разбитые на части
        var keywords = BuildKeywords(program);

        progress?.Report("Scanning registry...");
        traces.AddRange(await Task.Run(() => ScanRegistry(keywords), ct));

        progress?.Report("Scanning file system...");
        traces.AddRange(await Task.Run(() => ScanFileSystem(program, keywords, ct), ct));

        progress?.Report("Scanning startup entries...");
        traces.AddRange(await Task.Run(() => ScanStartupEntries(keywords), ct));

        progress?.Report("Scanning services...");
        traces.AddRange(await Task.Run(() => ScanServices(keywords), ct));

        progress?.Report("Scanning shortcuts...");
        traces.AddRange(await Task.Run(() => ScanShortcuts(keywords), ct));

        // Убираем дубли
        var unique = traces
            .GroupBy(t => t.Path.ToLowerInvariant())
            .Select(g => g.First())
            .OrderBy(t => t.Type)
            .ThenBy(t => t.Path)
            .ToList();

        progress?.Report($"Found {unique.Count} traces");
        return unique;
    }

    private static List<string> BuildKeywords(InstalledProgram p)
    {
        var kw = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Из имени — каждое слово длиннее 3 символов
        foreach (var word in p.Name.Split(' ', '-', '_', '.'))
            if (word.Length > 3) kw.Add(word);

        // Полное имя
        if (!string.IsNullOrEmpty(p.Name)) kw.Add(p.Name);

        // Издатель (первое слово)
        if (!string.IsNullOrEmpty(p.Publisher))
        {
            kw.Add(p.Publisher);
            var firstWord = p.Publisher.Split(' ')[0];
            if (firstWord.Length > 3) kw.Add(firstWord);
        }

        // Папка установки — имя папки
        if (!string.IsNullOrEmpty(p.InstallLocation))
        {
            var dirName = Path.GetFileName(p.InstallLocation.TrimEnd('\\', '/'));
            if (!string.IsNullOrEmpty(dirName)) kw.Add(dirName);
        }

        return kw.ToList();
    }

    // ── Registry scan ─────────────────────────────────────────────────────────

    private static List<UninstallTrace> ScanRegistry(List<string> keywords)
    {
        var traces = new List<UninstallTrace>();
        var searchPaths = new[]
        {
            (Registry.LocalMachine, @"SOFTWARE"),
            (Registry.CurrentUser, @"SOFTWARE"),
            (Registry.LocalMachine, @"SOFTWARE\WOW6432Node"),
        };

        foreach (var (hive, path) in searchPaths)
        {
            try
            {
                using var key = hive.OpenSubKey(path);
                if (key == null) continue;
                ScanRegistryKey(key, $@"{hive.Name}\{path}", keywords, traces, 0);
            }
            catch { }
        }
        return traces;
    }

    private static void ScanRegistryKey(RegistryKey key, string fullPath,
        List<string> keywords, List<UninstallTrace> traces, int depth)
    {
        if (depth > 3) return;

        // Проверяем имя ключа
        var keyName = Path.GetFileName(fullPath);
        if (MatchesAny(keyName, keywords))
        {
            traces.Add(new UninstallTrace
            {
                Type = "Registry",
                Path = fullPath,
                Description = $"Registry key: {keyName}"
            });
            return; // не идём глубже — весь ключ помечен
        }

        try
        {
            foreach (var subName in key.GetSubKeyNames().Take(100))
            {
                try
                {
                    using var sub = key.OpenSubKey(subName);
                    if (sub != null)
                        ScanRegistryKey(sub, $@"{fullPath}\{subName}", keywords, traces, depth + 1);
                }
                catch { }
            }
        }
        catch { }
    }

    // ── File system scan ──────────────────────────────────────────────────────

    private static List<UninstallTrace> ScanFileSystem(
        InstalledProgram program, List<string> keywords, CancellationToken ct)
    {
        var traces = new List<UninstallTrace>();

        var searchRoots = new List<string>();

        // Папка установки — первый приоритет
        if (!string.IsNullOrEmpty(program.InstallLocation) &&
            Directory.Exists(program.InstallLocation))
        {
            traces.Add(new UninstallTrace
            {
                Type = "Folder",
                Path = program.InstallLocation,
                Description = "Installation folder",
                SizeBytes = GetDirectorySize(program.InstallLocation),
                IsSelected = true
            });
        }

        // Стандартные места хранения данных
        searchRoots.AddRange(new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp"),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        });

        foreach (var root in searchRoots.Distinct())
        {
            if (ct.IsCancellationRequested) break;
            if (!Directory.Exists(root)) continue;
            if (program.InstallLocation != null &&
                root.StartsWith(program.InstallLocation, StringComparison.OrdinalIgnoreCase))
                continue; // уже добавили

            try
            {
                foreach (var dir in Directory.GetDirectories(root))
                {
                    if (ct.IsCancellationRequested) break;
                    var dirName = Path.GetFileName(dir);
                    if (!MatchesAny(dirName, keywords)) continue;
                    if (IsSystemPath(dir)) continue;

                    var size = GetDirectorySize(dir);
                    traces.Add(new UninstallTrace
                    {
                        Type = "Folder",
                        Path = dir,
                        Description = $"App data folder",
                        SizeBytes = size,
                        IsSelected = true
                    });
                }
            }
            catch { }
        }

        return traces;
    }

    // ── Startup scan ──────────────────────────────────────────────────────────

    private static List<UninstallTrace> ScanStartupEntries(List<string> keywords)
    {
        var traces = new List<UninstallTrace>();
        var regPaths = new[]
        {
            (Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run"),
            (Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce"),
            (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run"),
            (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run"),
        };

        foreach (var (hive, path) in regPaths)
        {
            try
            {
                using var key = hive.OpenSubKey(path);
                if (key == null) continue;
                foreach (var valueName in key.GetValueNames())
                {
                    var val = key.GetValue(valueName)?.ToString() ?? "";
                    if (MatchesAny(valueName, keywords) || MatchesAny(val, keywords))
                    {
                        traces.Add(new UninstallTrace
                        {
                            Type = "Startup",
                            Path = $@"{hive.Name}\{path}\{valueName}",
                            Description = $"Startup entry: {valueName} ({val})"
                        });
                    }
                }
            }
            catch { }
        }

        // Папки автозапуска
        var startupFolders = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Startup),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup),
        };
        foreach (var folder in startupFolders)
        {
            try
            {
                foreach (var file in Directory.GetFiles(folder, "*.lnk"))
                {
                    if (MatchesAny(Path.GetFileNameWithoutExtension(file), keywords))
                        traces.Add(new UninstallTrace
                        {
                            Type = "Shortcut",
                            Path = file,
                            Description = $"Startup shortcut: {Path.GetFileName(file)}"
                        });
                }
            }
            catch { }
        }

        return traces;
    }

    // ── Services scan ─────────────────────────────────────────────────────────

    private static List<UninstallTrace> ScanServices(List<string> keywords)
    {
        var traces = new List<UninstallTrace>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, DisplayName, PathName FROM Win32_Service");
            foreach (ManagementObject svc in searcher.Get())
            {
                var name = svc["Name"]?.ToString() ?? "";
                var displayName = svc["DisplayName"]?.ToString() ?? "";
                var path = svc["PathName"]?.ToString() ?? "";

                if (MatchesAny(name, keywords) ||
                    MatchesAny(displayName, keywords) ||
                    MatchesAny(path, keywords))
                {
                    traces.Add(new UninstallTrace
                    {
                        Type = "Service",
                        Path = $@"HKLM\SYSTEM\CurrentControlSet\Services\{name}",
                        Description = $"Windows Service: {displayName} ({name})",
                        IsDangerous = IsSystemService(name)
                    });
                }
            }
        }
        catch { }
        return traces;
    }

    // ── Shortcuts scan ────────────────────────────────────────────────────────

    private static List<UninstallTrace> ScanShortcuts(List<string> keywords)
    {
        var traces = new List<UninstallTrace>();
        var folders = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
        };

        foreach (var folder in folders)
        {
            try
            {
                foreach (var file in Directory.GetFiles(folder, "*.lnk", SearchOption.AllDirectories))
                {
                    if (MatchesAny(Path.GetFileNameWithoutExtension(file), keywords))
                        traces.Add(new UninstallTrace
                        {
                            Type = "Shortcut",
                            Path = file,
                            Description = $"Shortcut: {Path.GetFileName(file)}"
                        });
                }
            }
            catch { }
        }
        return traces;
    }

    // ── Run standard uninstaller ──────────────────────────────────────────────

    public async Task<bool> RunStandardUninstallerAsync(InstalledProgram program)
    {
        if (string.IsNullOrEmpty(program.UninstallString)) return false;

        return await Task.Run(() =>
        {
            try
            {
                // Некоторые строки содержат аргументы типа MsiExec.exe /I{GUID}
                var uninstallStr = program.UninstallString;
                string fileName, args;

                if (uninstallStr.StartsWith("\""))
                {
                    var endQuote = uninstallStr.IndexOf('"', 1);
                    fileName = uninstallStr[1..endQuote];
                    args = uninstallStr[(endQuote + 1)..].Trim();
                }
                else
                {
                    var spaceIdx = uninstallStr.IndexOf(' ');
                    if (spaceIdx > 0)
                    {
                        fileName = uninstallStr[..spaceIdx];
                        args = uninstallStr[(spaceIdx + 1)..];
                    }
                    else
                    {
                        fileName = uninstallStr;
                        args = "";
                    }
                }

                // Для MSI добавляем /qb для тихой деинсталляции
                if (fileName.Contains("msiexec", StringComparison.OrdinalIgnoreCase) &&
                    !args.Contains("/q"))
                    args += " /qb";

                var psi = new ProcessStartInfo(fileName, args)
                {
                    UseShellExecute = true // нужен для UAC elevation
                };
                var p = Process.Start(psi);
                p?.WaitForExit(120_000); // 2 минуты
                return true;
            }
            catch { return false; }
        });
    }

    // ── Remove selected traces ────────────────────────────────────────────────

    public async Task<UninstallResult> RemoveTracesAsync(
        List<UninstallTrace> traces,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var result = new UninstallResult();

        await Task.Run(() =>
        {
            foreach (var trace in traces.Where(t => t.IsSelected))
            {
                if (ct.IsCancellationRequested) break;

                try
                {
                    // Самозащита: следы, указывающие на нас самих, не трогаем
                    if (SelfProtection.IsProtectedPath(trace.Path))
                    {
                        result.Errors.Add($"{trace.Path}: skipped (protected SystemGuard path)");
                        continue;
                    }
                    progress?.Report($"Removing: {trace.Path}");

                    switch (trace.Type)
                    {
                        case "Folder":
                            if (Directory.Exists(trace.Path))
                            {
                                result.FreedBytes += GetDirectorySize(trace.Path);
                                if (!SelfProtection.SafeDeleteDirectory(trace.Path, true))
                                    throw new InvalidOperationException("refused by self-protection");
                                result.Removed.Add(trace.Path);
                                result.RemovedCount++;
                            }
                            break;

                        case "File":
                        case "Shortcut":
                            if (File.Exists(trace.Path))
                            {
                                result.FreedBytes += new FileInfo(trace.Path).Length;
                                if (!SelfProtection.SafeDeleteFile(trace.Path))
                                    throw new InvalidOperationException("refused by self-protection");
                                result.Removed.Add(trace.Path);
                                result.RemovedCount++;
                            }
                            break;

                        case "Registry":
                        case "Startup":
                            RemoveRegistryEntry(trace.Path);
                            result.Removed.Add(trace.Path);
                            result.RemovedCount++;
                            break;

                        case "Service":
                            StopAndRemoveService(trace);
                            result.Removed.Add(trace.Path);
                            result.RemovedCount++;
                            break;
                    }
                }
                catch (Exception ex)
                {
                    result.Errors.Add($"{trace.Path}: {ex.Message}");
                }
            }
        }, ct);

        return result;
    }

    private static void RemoveRegistryEntry(string fullPath)
    {
        // Парсим hive из полного пути
        var parts = fullPath.Split('\\', 3);
        if (parts.Length < 3) return;

        var hive = parts[0] switch
        {
            "HKEY_LOCAL_MACHINE" or "HKLM" => Registry.LocalMachine,
            "HKEY_CURRENT_USER" or "HKCU" => Registry.CurrentUser,
            _ => null
        };
        if (hive == null) return;

        var subKeyPath = parts[1];
        var keyName = parts.Length > 2 ? parts[2] : "";

        // Если путь содержит имя значения — удаляем значение
        var lastBackslash = keyName.LastIndexOf('\\');
        if (lastBackslash >= 0)
        {
            var parentPath = $@"{subKeyPath}\{keyName[..lastBackslash]}";
            var valueName = keyName[(lastBackslash + 1)..];
            using var key = hive.OpenSubKey(parentPath, true);
            key?.DeleteValue(valueName, false);
        }
        else
        {
            // Удаляем весь ключ
            using var parent = hive.OpenSubKey(subKeyPath, true);
            parent?.DeleteSubKeyTree(keyName, false);
        }
    }

    private static void StopAndRemoveService(UninstallTrace trace)
    {
        var parts = trace.Path.Split('\\');
        var serviceName = parts[^1];
        try
        {
            using var sc = new System.ServiceProcess.ServiceController(serviceName);
            if (sc.Status != System.ServiceProcess.ServiceControllerStatus.Stopped)
            {
                sc.Stop();
                sc.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Stopped,
                    TimeSpan.FromSeconds(10));
            }
        }
        catch { }

        // sc delete
        try
        {
            using var p = Process.Start(new ProcessStartInfo("sc", $"delete \"{serviceName}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            });
            p?.WaitForExit(5000);
        }
        catch { }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static bool MatchesAny(string text, List<string> keywords)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        return keywords.Any(k => text.Contains(k, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsSystemPath(string path)
        => _safeSkipPaths.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    private static bool IsSystemService(string name)
    {
        var systemServices = new[]
        {
            "wuauserv","WSearch","Spooler","BITS","SysMain","FontCache",
            "DiagTrack","lsass","winlogon","csrss","smss","svchost",
            "ntoskrnl","services","wininit","lsm"
        };
        return systemServices.Contains(name, StringComparer.OrdinalIgnoreCase);
    }

    private static long GetDirectorySize(string path)
    {
        long size = 0;
        try
        {
            foreach (var file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
                try { size += new FileInfo(file).Length; } catch { }
        }
        catch { }
        return size;
    }
}
