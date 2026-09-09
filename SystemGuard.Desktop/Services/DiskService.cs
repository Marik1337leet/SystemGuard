using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace SystemGuard.Desktop.Services;

public class DiskInfoModel
{
    public string DriveLetter { get; set; } = "";
    public string Label { get; set; } = "";
    public string FileSystem { get; set; } = "";
    public long TotalSize { get; set; }
    public long FreeSpace { get; set; }
    public long UsedSpace => TotalSize - FreeSpace;
    public double UsagePercent => TotalSize > 0 ? Math.Round((double)UsedSpace / TotalSize * 100, 1) : 0;
    public string Model { get; set; } = "";
    public bool IsSSD { get; set; }
    public int Temperature { get; set; }
    public string HealthStatus { get; set; } = "OK";
}

public class FolderSizeInfo
{
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public long Size { get; set; }
}

public class LargeFileInfo
{
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public long Size { get; set; }
}

public class DuplicateFileGroup
{
    public string Hash { get; set; } = "";
    public long Size { get; set; }
    public List<string> Paths { get; set; } = new();
    public long WastedSpace => Size * (Paths.Count - 1);
}

public class DiskService
{
    public List<DiskInfoModel> GetDrives()
    {
        var drives = new List<DiskInfoModel>();
        try
        {
            foreach (var drive in System.IO.DriveInfo.GetDrives())
            {
                try
                {
                    if (drive.IsReady)
                    {
                        drives.Add(new DiskInfoModel
                        {
                            DriveLetter = drive.Name,
                            Label = drive.VolumeLabel,
                            FileSystem = drive.DriveFormat,
                            TotalSize = drive.TotalSize,
                            FreeSpace = drive.TotalFreeSpace,
                            Model = drive.DriveType.ToString(),
                            IsSSD = drive.DriveType == System.IO.DriveType.Fixed
                        });
                    }
                }
                catch { }
            }
        }
        catch { }
        return drives;
    }

    public async Task<List<FolderSizeInfo>> AnalyzeFolderSizes(string rootPath, int maxDepth = 3)
    {
        return await Task.Run(() =>
        {
            var result = new List<FolderSizeInfo>();
            try
            {
                if (!Directory.Exists(rootPath)) return result;
                foreach (var dir in Directory.GetDirectories(rootPath).Take(20))
                {
                    try
                    {
                        result.Add(new FolderSizeInfo
                        {
                            Path = dir,
                            Name = Path.GetFileName(dir),
                            Size = GetDirectorySize(dir, maxDepth)
                        });
                    }
                    catch { }
                }
            }
            catch { }
            return result.OrderByDescending(x => x.Size).ToList();
        });
    }

    private long GetDirectorySize(string path, int depth)
    {
        if (depth <= 0) return 0;
        long size = 0;
        try
        {
            foreach (var file in Directory.GetFiles(path))
                try { size += new FileInfo(file).Length; } catch { }
            foreach (var dir in Directory.GetDirectories(path).Take(10))
                size += GetDirectorySize(dir, depth - 1);
        }
        catch { }
        return size;
    }

    public async Task<List<LargeFileInfo>> FindLargeFiles(string rootPath, long minSizeBytes = 100 * 1024 * 1024)
    {
        return await Task.Run(() =>
        {
            var files = new List<LargeFileInfo>();
            try
            {
                foreach (var file in SafeGetFiles(rootPath).Take(1000))
                {
                    try
                    {
                        var fi = new FileInfo(file);
                        if (fi.Length >= minSizeBytes)
                            files.Add(new LargeFileInfo { Path = file, Name = fi.Name, Size = fi.Length });
                    }
                    catch { }
                }
            }
            catch { }
            return files.OrderByDescending(x => x.Size).ToList();
        });
    }

    private IEnumerable<string> SafeGetFiles(string path)
    {
        var files = new List<string>();
        try { files.AddRange(Directory.GetFiles(path)); } catch { }
        try
        {
            foreach (var dir in Directory.GetDirectories(path).Take(50))
                files.AddRange(SafeGetFiles(dir));
        }
        catch { }
        return files;
    }

    public async Task<List<DuplicateFileGroup>> FindDuplicates(string rootPath)
    {
        return await Task.Run(() =>
        {
            var sizeGroups = new Dictionary<long, List<string>>();
            foreach (var file in SafeGetFiles(rootPath).Take(5000))
            {
                try
                {
                    var fi = new FileInfo(file);
                    if (!sizeGroups.ContainsKey(fi.Length))
                        sizeGroups[fi.Length] = new List<string>();
                    sizeGroups[fi.Length].Add(file);
                }
                catch { }
            }

            var duplicates = new List<DuplicateFileGroup>();
            foreach (var group in sizeGroups.Where(g => g.Value.Count > 1))
            {
                var hashGroups = new Dictionary<string, List<string>>();
                foreach (var file in group.Value)
                {
                    try
                    {
                        var hash = CalculateQuickHash(file);
                        if (!hashGroups.ContainsKey(hash))
                            hashGroups[hash] = new List<string>();
                        hashGroups[hash].Add(file);
                    }
                    catch { }
                }
                foreach (var hg in hashGroups.Where(h => h.Value.Count > 1))
                {
                    duplicates.Add(new DuplicateFileGroup
                    {
                        Hash = hg.Key,
                        Size = group.Key,
                        Paths = hg.Value
                    });
                }
            }
            return duplicates.OrderByDescending(d => d.WastedSpace).Take(100).ToList();
        });
    }

    private string CalculateQuickHash(string filePath)
    {
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read);
        var buffer = new byte[4096];
        fs.Read(buffer, 0, buffer.Length);
        var hashBytes = System.Security.Cryptography.SHA256.HashData(buffer);
        return Convert.ToBase64String(hashBytes) + "_" + fs.Length;
    }
}