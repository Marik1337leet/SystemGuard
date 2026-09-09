using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace SystemGuard.Desktop.Services;

public partial class CleanupItem : ObservableObject
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public long Size { get; set; }
    [ObservableProperty] private bool _isSelected = true;
    public string Category { get; set; } = "";
}

public class AdvancedCleanupResult
{
    public long TotalCleaned { get; set; }
    public int FilesDeleted { get; set; }
    public List<string> Errors { get; set; } = new();
}

public class AdvancedCleanupService
{
    public List<CleanupItem> GetCleanupItems()
    {
        var items = new List<CleanupItem>();
        items.AddRange(GetTempFiles());
        items.AddRange(GetBrowserCaches());
        items.AddRange(GetWindowsCaches());
        items.AddRange(GetMessengerCaches());
        items.AddRange(GetGameCaches());
        items.AddRange(GetCloudCaches());
        items.AddRange(GetMailCaches());
        items.AddRange(GetStreamingCaches());
        return items;
    }

    private List<CleanupItem> GetTempFiles()
    {
        var items = new List<CleanupItem>();
        var tempPaths = new[] { Path.GetTempPath(), @"C:\Windows\Temp" };
        foreach (var path in tempPaths)
        {
            try
            {
                if (!Directory.Exists(path)) continue;
                var size = GetDirectorySize(path);
                if (size > 0)
                    items.Add(new CleanupItem { Name = "Temp Files", Path = path, Size = size, Category = "Temporary Files" });
            }
            catch { }
        }
        return items;
    }

    private List<CleanupItem> GetBrowserCaches()
    {
        var items = new List<CleanupItem>();
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var browsers = new Dictionary<string, string>
        {
            ["Chrome"] = local + "\\Google\\Chrome\\User Data\\Default\\Cache\\Cache_Data",
            ["Edge"] = local + "\\Microsoft\\Edge\\User Data\\Default\\Cache\\Cache_Data",
            ["Brave"] = local + "\\BraveSoftware\\Brave-Browser\\User Data\\Default\\Cache\\Cache_Data",
            ["Opera"] = roaming + "\\Opera Software\\Opera Stable\\Cache\\Cache_Data",
            ["Opera GX"] = roaming + "\\Opera Software\\Opera GX Stable\\Cache\\Cache_Data",
            ["Firefox"] = local + "\\Mozilla\\Firefox\\Profiles",
            ["Vivaldi"] = local + "\\Vivaldi\\User Data\\Default\\Cache\\Cache_Data",
        };
        foreach (var browser in browsers)
        {
            try
            {
                if (!Directory.Exists(browser.Value)) continue;
                var size = GetDirectorySize(browser.Value);
                if (size > 0)
                    items.Add(new CleanupItem { Name = $"{browser.Key} Cache", Path = browser.Value, Size = size, Category = "Browser Cache" });
            }
            catch { }
        }
        return items;
    }

    private List<CleanupItem> GetWindowsCaches()
    {
        var items = new List<CleanupItem>();
        var paths = new Dictionary<string, string>
        {
            ["Windows Update Cache"] = @"C:\Windows\SoftwareDistribution\Download",
            ["Prefetch"] = @"C:\Windows\Prefetch"
        };
        foreach (var cache in paths)
        {
            try
            {
                if (!Directory.Exists(cache.Value)) continue;
                var size = GetDirectorySize(cache.Value);
                if (size > 0)
                    items.Add(new CleanupItem { Name = cache.Key, Path = cache.Value, Size = size, Category = "Windows Cache" });
            }
            catch { }
        }
        return items;
    }

    private List<CleanupItem> GetMessengerCaches()
    {
        var items = new List<CleanupItem>();
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var paths = new Dictionary<string, string>
        {
            ["Telegram Cache"] = roaming + "\\Telegram Desktop\\tdata\\user_data\\cache",
            ["Telegram Media"] = Path.Combine(userProfile, "Downloads\\Telegram Desktop"),
            ["WhatsApp Cache"] = local + "\\Packages\\5319275A.WhatsAppDesktop_cv1g1gvanyjgm\\LocalState\\Cache",
            ["WhatsApp Media"] = roaming + "\\WhatsApp\\Cache",
            ["Discord Cache"] = roaming + "\\discord\\Cache",
            ["Viber Cache"] = roaming + "\\ViberPC\\Cache",
        };
        foreach (var kv in paths)
        {
            try
            {
                if (!Directory.Exists(kv.Value)) continue;
                var size = GetDirectorySize(kv.Value);
                if (size > 0)
                    items.Add(new CleanupItem { Name = kv.Key, Path = kv.Value, Size = size, Category = "Messenger Cache" });
            }
            catch { }
        }
        return items;
    }

    private List<CleanupItem> GetGameCaches()
    {
        var items = new List<CleanupItem>();
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var paths = new Dictionary<string, string>
        {
            ["Steam Shader Cache"] = programFiles + "\\Steam\\steamapps\\shadercache",
            ["Steam Depot Cache"] = programFiles + "\\Steam\\depotcache",
            ["Epic Shader Cache"] = local + "\\EpicGamesLauncher\\Saved\\webcache",
            ["Epic Web Cache"] = local + "\\EpicGamesLauncher\\Saved\\webcache_4430",
            ["GOG Galaxy Cache"] = local + "\\GOG.com\\Galaxy\\webcache",
            ["Minecraft Logs/Crash"] = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) + "\\.minecraft\\logs",
        };
        foreach (var kv in paths)
        {
            try
            {
                if (!Directory.Exists(kv.Value)) continue;
                var size = GetDirectorySize(kv.Value);
                if (size > 0)
                    items.Add(new CleanupItem { Name = kv.Key, Path = kv.Value, Size = size, Category = "Game Cache" });
            }
            catch { }
        }
        return items;
    }

    private long GetDirectorySize(string path)
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

    // Облачные хранилища: ТОЛЬКО кэши/логи, сами синхронизированные файлы не трогаем.
    private List<CleanupItem> GetCloudCaches()
    {
        var items = new List<CleanupItem>();
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var paths = new Dictionary<string, string>
        {
            ["Dropbox Cache"] = local + "\\Dropbox\\cache",
            ["Google DriveFS Logs"] = local + "\\Google\\DriveFS\\Logs",
            ["OneDrive Logs"] = local + "\\Microsoft\\OneDrive\\logs",
            ["OneDrive Setup Logs"] = local + "\\Microsoft\\OneDrive\\setup\\logs",
        };
        foreach (var kv in paths)
        {
            try
            {
                if (!Directory.Exists(kv.Value)) continue;
                var size = GetDirectorySize(kv.Value);
                if (size > 0)
                    items.Add(new CleanupItem { Name = kv.Key, Path = kv.Value, Size = size, Category = "Cloud Cache" });
            }
            catch { }
        }
        return items;
    }

    // Почта: кэши и временные вложения, НЕ хранилища писем (.ost/.pst не трогаем).
    private List<CleanupItem> GetMailCaches()
    {
        var items = new List<CleanupItem>();
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var paths = new Dictionary<string, string>
        {
            ["Thunderbird Cache"] = roaming + "\\Thunderbird\\Profiles",
            ["Outlook Temp Attachments"] = local + "\\Microsoft\\Windows\\INetCache\\Content.Outlook",
            ["Windows Mail Temp"] = local + "\\Packages\\microsoft.windowscommunicationsapps_8wekyb3d8bbwe\\LocalState",
        };
        foreach (var kv in paths)
        {
            try
            {
                if (!Directory.Exists(kv.Value)) continue;
                var size = kv.Key.Contains("Thunderbird")
                    ? GetThunderbirdCacheSize(kv.Value)
                    : GetDirectorySize(kv.Value);
                if (size > 0)
                    items.Add(new CleanupItem { Name = kv.Key, Path = kv.Value, Size = size, Category = "Mail Cache" });
            }
            catch { }
        }
        return items;
    }

    private static long GetThunderbirdCacheSize(string profilesDir)
    {
        long size = 0;
        try
        {
            foreach (var d in Directory.GetDirectories(profilesDir))
                foreach (var cache in new[] { "cache2", "startupCache" })
                {
                    var p = Path.Combine(d, cache);
                    if (Directory.Exists(p))
                        foreach (var f in Directory.GetFiles(p, "*", SearchOption.AllDirectories))
                            try { size += new FileInfo(f).Length; } catch { }
                }
        }
        catch { }
        return size;
    }

    // Стриминги: кэши приложений (офлайн-контент не удаляем — только cache-папки).
    private List<CleanupItem> GetStreamingCaches()
    {
        var items = new List<CleanupItem>();
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var paths = new Dictionary<string, string>
        {
            ["Spotify Cache"] = local + "\\Spotify\\Browser\\Cache",
            ["Spotify Data Cache"] = roaming + "\\Spotify\\cache",
            ["Netflix App Cache"] = local + "\\Packages\\4DF9E0F8.Netflix_mcm4njqhnhss8\\LocalState\\cache",
            ["YouTube Music Cache"] = local + "\\Google\\Chrome\\User Data\\Default\\Cache\\Cache_Data",
        };
        foreach (var kv in paths)
        {
            try
            {
                if (!Directory.Exists(kv.Value)) continue;
                if (kv.Key == "YouTube Music Cache") continue; // уже покрыт кэшем Chrome
                var size = GetDirectorySize(kv.Value);
                if (size > 0)
                    items.Add(new CleanupItem { Name = kv.Key, Path = kv.Value, Size = size, Category = "Streaming Cache" });
            }
            catch { }
        }
        return items;
    }

    // NTFS-сжатие папки (compact.exe): дедупликация места без удаления.
    public static string CompactFolder(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return "Folder not found";
            if (SelfProtection.IsProtectedPath(path)) return "Protected path — refused";
            var psi = new System.Diagnostics.ProcessStartInfo("compact.exe", $"/C /S:\"{path}\"")
            { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p == null) return "Cannot start compact.exe";
            var out1 = p.StandardOutput.ReadToEnd();
            p.WaitForExit(120000);
            return string.IsNullOrWhiteSpace(out1) ? "Compressed" : out1.Trim();
        }
        catch (Exception ex) { return $"Compact failed: {ex.Message}"; }
    }

    public async Task<AdvancedCleanupResult> CleanItems(List<CleanupItem> items)
    {
        var result = new AdvancedCleanupResult();
        await Task.Run(() =>
        {
            foreach (var item in items.Where(i => i.IsSelected))
            {
                try
                {
                    // Самозащита: своё не удаляем никогда
                    if (SelfProtection.IsProtectedPath(item.Path))
                    {
                        result.Errors.Add($"{item.Name}: skipped (protected SystemGuard path)");
                        continue;
                    }
                    if (Directory.Exists(item.Path))
                    {
                        result.FilesDeleted += Directory.GetFiles(item.Path, "*", SearchOption.AllDirectories).Length;
                        result.TotalCleaned += item.Size;
                        if (!SelfProtection.SafeDeleteDirectory(item.Path, true))
                            result.Errors.Add($"{item.Name}: delete refused by self-protection");
                    }
                    else if (File.Exists(item.Path))
                    {
                        result.FilesDeleted++;
                        result.TotalCleaned += new FileInfo(item.Path).Length;
                        if (!SelfProtection.SafeDeleteFile(item.Path))
                            result.Errors.Add($"{item.Name}: delete refused by self-protection");
                    }
                }
                catch (Exception ex) { result.Errors.Add($"{item.Name}: {ex.Message}"); }
            }
        });
        return result;
    }

    // Корзина — нативный SHEmptyRecycleBin, без подтверждения, без звука
    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? root, uint flags);
    private const uint SHERB_NOCONFIRMATION = 0x1, SHERB_NOPROGRESSUI = 0x2, SHERB_NOSOUND = 0x4;

    public static string EmptyRecycleBin()
    {
        try
        {
            int hr = SHEmptyRecycleBin(IntPtr.Zero, null,
                SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND);
            return hr == 0 ? "Recycle Bin emptied" : "Recycle Bin is already empty";
        }
        catch (Exception ex) { return $"Recycle Bin error: {ex.Message}"; }
    }
}