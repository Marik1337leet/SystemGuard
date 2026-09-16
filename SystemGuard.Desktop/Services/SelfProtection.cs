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

    // ── Remote-чтение (/api/file, бот /get, листинг): что НЕ отдаём наружу ──
    // Даже с валидным токеном нельзя скачать: папку данных SystemGuard
    // (bot-токены, live-токен, license.dat, supabase.json, продажи),
    // каталог Windows (SAM/SYSTEM хайвы, credentials) и профили ЧУЖИХ
    // пользователей. Свои документы/файлы — можно.
    // Возвращает null если можно, иначе причину блокировки.
    public static string? BlockedForRemoteRead(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "Empty path";
        string full;
        try { full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar); }
        catch { return "Bad path"; }

        if (IsProtectedPath(path)) return "SystemGuard data folder is hidden";

        try
        {
            var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (!string.IsNullOrEmpty(win))
            {
                var w = Path.GetFullPath(win).TrimEnd(Path.DirectorySeparatorChar);
                if (full.Equals(w, StringComparison.OrdinalIgnoreCase) ||
                    full.StartsWith(w + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    return "Windows folder is hidden";
            }
        }
        catch { }

        try
        {
            // C:\Users\<чужой>\... — чужие профили скрыты, свой разрешён.
            var usersRoot = Path.GetFullPath(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".."))
                .TrimEnd(Path.DirectorySeparatorChar);
            var ownProfile = Path.GetFullPath(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))
                .TrimEnd(Path.DirectorySeparatorChar);
            if (full.StartsWith(usersRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                !full.Equals(ownProfile, StringComparison.OrdinalIgnoreCase) &&
                !full.StartsWith(ownProfile + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                !full.StartsWith(Path.Combine(usersRoot, "Public") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return "Other users profiles are hidden";
        }
        catch { }

        return null;
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
