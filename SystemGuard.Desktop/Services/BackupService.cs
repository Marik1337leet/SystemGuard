using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace SystemGuard.Desktop.Services;

public class BackupEntry
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public DateTime Created { get; set; }
    public bool IsAuto { get; set; }
}

// Бэкапы настроек: автобэкап при каждом запуске (храним последние 3),
// пользовательские бэкапы не удаляются автоматически.
public class BackupService
{
    private readonly string _appDir;
    private readonly string _backupDir;

    public BackupService()
    {
        _appDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SystemGuard");
        Directory.CreateDirectory(_appDir);
        _backupDir = Path.Combine(_appDir, "backups");
        Directory.CreateDirectory(_backupDir);
    }

    public BackupEntry AutoBackupOnStartup()
    {
        var entry = CreateBackup(auto: true);
        PruneAutoBackups(keepLast: 3);
        return entry;
    }

    public BackupEntry CreateBackup(bool auto)
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var name = auto ? $"auto_{stamp}" : $"manual_{stamp}";
        var dir = Path.Combine(_backupDir, name);
        Directory.CreateDirectory(dir);

        foreach (var file in new[] { "settings.json", "theme.json", "theme_profiles.json", "scheduled_tasks.json", "widgets.json" })
        {
            try
            {
                var src = Path.Combine(_appDir, file);
                if (File.Exists(src)) File.Copy(src, Path.Combine(dir, file), true);
            }
            catch { }
        }

        return new BackupEntry { Name = name, Path = dir, Created = DateTime.Now, IsAuto = auto };
    }

    public List<BackupEntry> ListBackups()
    {
        var list = new List<BackupEntry>();
        try
        {
            foreach (var dir in Directory.GetDirectories(_backupDir))
            {
                var name = Path.GetFileName(dir);
                list.Add(new BackupEntry
                {
                    Name = name,
                    Path = dir,
                    Created = Directory.GetCreationTime(dir),
                    IsAuto = name.StartsWith("auto_", StringComparison.OrdinalIgnoreCase)
                });
            }
        }
        catch { }
        return list.OrderByDescending(b => b.Created).ToList();
    }

    public void Restore(BackupEntry backup)
    {
        foreach (var file in Directory.GetFiles(backup.Path))
        {
            try
            {
                var dest = Path.Combine(_appDir, Path.GetFileName(file));
                if (SelfProtection.IsProtectedPath(dest)) continue;
                File.Copy(file, dest, true);
            }
            catch { }
        }
    }

    private void PruneAutoBackups(int keepLast)
    {
        var autos = ListBackups().Where(b => b.IsAuto).ToList();
        foreach (var old in autos.Skip(keepLast))
        {
            try { Directory.Delete(old.Path, true); } catch { }
        }
    }
}
