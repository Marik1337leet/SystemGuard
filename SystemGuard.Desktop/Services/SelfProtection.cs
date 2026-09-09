using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SystemGuard.Desktop.Services;

// ── Централизованная самозащита ─────────────────────────────────────────────
// Единственное место, где перечислено "своё": процесс, папка установки,
// папка данных. ВСЕ destructive-операции (чистка, удаление, kill) обязаны
// сверяться с этим классом. Исключает целый класс багов:
// "приложение очистило/удалило/убило само себя" и последующий краш.
public static class SelfProtection
{
    private static readonly HashSet<string> _protectedProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "SystemGuard", "SystemGuard.Desktop", "SystemGuard.KeyGenerator",
        "explorer", "dwm", "System", "Registry", "csrss", "wininit",
        "winlogon", "services", "lsass", "lsaiso", "smss", "fontdrvhost",
        "ShellExperienceHost", "SearchHost", "StartMenuExperienceHost",
        "TextInputHost", "RuntimeBroker", "sihost", "taskhostw"
    };

    private static string? _appDir;
    private static string? _dataDir;

    public static string AppDirectory => _appDir ??= AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
    public static string DataDirectory => _dataDir ??= Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SystemGuard");

    public static bool IsProtectedProcess(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return true; // неизвестное — не трогаем
        name = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? name[..^4] : name;
        if (_protectedProcessNames.Contains(name)) return true;
        // Текущий процесс и его имя файла — всегда защищены
        try
        {
            var self = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(self) &&
                string.Equals(Path.GetFileNameWithoutExtension(self), name, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        catch { }
        return false;
    }

    public static bool IsProtectedProcessId(int pid)
    {
        try
        {
            if (pid == Environment.ProcessId) return true; // сам себя
            if (pid <= 4) return true; // System / Idle
            var name = System.Diagnostics.Process.GetProcessById(pid).ProcessName;
            return IsProtectedProcess(name);
        }
        catch { return true; } // нет доступа — не трогаем
    }

    // True, если путь — это само приложение, его папка или папка данных.
    public static bool IsProtectedPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return true;
        try
        {
            var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
            var app = Path.GetFullPath(AppDirectory).TrimEnd(Path.DirectorySeparatorChar);
            var data = Path.GetFullPath(DataDirectory).TrimEnd(Path.DirectorySeparatorChar);

            if (full.Equals(app, StringComparison.OrdinalIgnoreCase)) return true;
            if (full.Equals(data, StringComparison.OrdinalIgnoreCase)) return true;
            if (full.StartsWith(app + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return true;
            if (full.StartsWith(data + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return true;

            // Сам .exe и его имя в любом виде
            var self = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(self) &&
                full.Equals(Path.GetFullPath(self).TrimEnd(Path.DirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase)) return true;
        }
        catch { return true; }
        return false;
    }

    public static bool IsProtectedProgram(string? name, string? installLocation)
    {
        if (!string.IsNullOrWhiteSpace(name) &&
            name.Contains("SystemGuard", StringComparison.OrdinalIgnoreCase))
            return true;
        if (!string.IsNullOrWhiteSpace(installLocation) && IsProtectedPath(installLocation))
            return true;
        return false;
    }

    // Безопасное удаление: возвращает false и ничего не делает для защищённых путей.
    public static bool SafeDeleteFile(string path)
    {
        try
        {
            if (IsProtectedPath(path)) return false;
            if (!File.Exists(path)) return false;
            File.Delete(path);
            return true;
        }
        catch { return false; }
    }

    public static bool SafeDeleteDirectory(string path, bool recursive = true)
    {
        try
        {
            if (IsProtectedPath(path)) return false;
            if (!Directory.Exists(path)) return false;
            // Доп. страховка: внутри нет нашего exe
            var self = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(self))
            {
                var selfFull = Path.GetFullPath(self);
                if (selfFull.StartsWith(Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar)
                        + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            Directory.Delete(path, recursive);
            return true;
        }
        catch { return false; }
    }
}
