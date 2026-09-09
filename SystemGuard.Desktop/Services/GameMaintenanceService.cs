using System;
using System.Collections.Generic;
using System.IO;

namespace SystemGuard.Desktop.Services;

// Игровое обслуживание: чистка кэша шейдеров Steam/Epic, бэкап/восстановление сейвов.
public static class GameMaintenanceService
{
    public static string CleanShaderCaches()
    {
        long freed = 0;
        int removed = 0;
        var paths = new List<string>();
        try
        {
            var steam = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam/steamapps/shadercache");
            if (Directory.Exists(steam)) paths.Add(steam);
            var dxcache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "D3DSCache");
            if (Directory.Exists(dxcache)) paths.Add(dxcache);
            var nvidia = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NVIDIA/DxCache");
            if (Directory.Exists(nvidia)) paths.Add(nvidia);
        }
        catch { }

        foreach (var dir in paths)
        {
            try
            {
                foreach (var file in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        if (SelfProtection.IsProtectedPath(file)) continue;
                        freed += new FileInfo(file).Length;
                        File.Delete(file);
                        removed++;
                    }
                    catch { }
                }
            }
            catch { }
        }
        return removed == 0 ? "No shader caches found" : $"Shader caches cleaned: {removed} files, {freed / 1048576.0:F1} MB";
    }

    public static string BackupSaves(string savesDir, string backupRoot)
    {
        try
        {
            if (!Directory.Exists(savesDir)) return "Saves folder not found";
            Directory.CreateDirectory(backupRoot);
            var dest = Path.Combine(backupRoot, $"saves_{DateTime.Now:yyyyMMdd_HHmmss}");
            CopyDir(savesDir, dest);
            return $"Saves backed up: {dest}";
        }
        catch (Exception ex) { return $"Backup failed: {ex.Message}"; }
    }

    private static void CopyDir(string src, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.GetFiles(src))
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), true);
        foreach (var dir in Directory.GetDirectories(src))
            CopyDir(dir, Path.Combine(dest, Path.GetFileName(dir)));
    }
}
