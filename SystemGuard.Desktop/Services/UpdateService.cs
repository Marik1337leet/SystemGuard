using System;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;

namespace SystemGuard.Desktop.Services;

public class UpdateCheckResult
{
    public bool Success { get; set; }
    public bool HasUpdate { get; set; }
    public string Current { get; set; } = "";
    public string Latest { get; set; } = "";
    public string ReleaseUrl { get; set; } = "";
    public string SetupAssetUrl { get; set; } = "";
    public string SetupAssetName { get; set; } = "";
    public string Error { get; set; } = "";
}

// Реальная проверка обновлений через GitHub Releases API. Без заглушек:
// ходит в сеть, парсит latest release, сравнивает версии, обрабатывает офлайн.
public class UpdateService
{
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    public const string DefaultRepo = "marik1337leet/SystemGuard";

    public static string CurrentVersion()
    {
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        return v != null ? $"{v.Major}.{v.Minor}.{v.Build}" : "1.0.0";
    }

    public async Task<UpdateCheckResult> CheckAsync(string repo = DefaultRepo)
    {
        var res = new UpdateCheckResult { Current = CurrentVersion() };
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{repo}/releases/latest");
            req.Headers.UserAgent.ParseAdd("SystemGuard-Updater");
            req.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var resp = await _http.SendAsync(req);
            if (!resp.IsSuccessStatusCode)
            {
                res.Success = false;
                res.Error = $"GitHub API: {(int)resp.StatusCode}";
                return res;
            }
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
            res.Latest = tag.TrimStart('v', 'V');
            res.ReleaseUrl = root.TryGetProperty("html_url", out var u) ? u.GetString() ?? "" : $"https://github.com/{repo}/releases/latest";
            // Ищем установщик среди ассетов релиза: SystemGuard-Setup-*.exe
            try
            {
                if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                {
                    foreach (var a in assets.EnumerateArray())
                    {
                        var name = a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                        var url = a.TryGetProperty("browser_download_url", out var d) ? d.GetString() ?? "" : "";
                        if (name.StartsWith("SystemGuard-Setup-", StringComparison.OrdinalIgnoreCase)
                            && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                            && !string.IsNullOrEmpty(url))
                        {
                            res.SetupAssetName = name;
                            res.SetupAssetUrl = url;
                            break;
                        }
                    }
                }
            }
            catch { }
            res.Success = true;
            res.HasUpdate = IsNewer(res.Latest, res.Current);
            return res;
        }
        catch (Exception ex)
        {
            res.Success = false;
            res.Error = ex.Message;
            return res;
        }
    }

    public static bool IsNewer(string latest, string current)
    {
        try
        {
            var l = latest.Split('.');
            var c = current.Split('.');
            for (int i = 0; i < Math.Max(l.Length, c.Length); i++)
            {
                int lv = i < l.Length && int.TryParse(new string(l[i].TakeWhile(char.IsDigit).ToArray()), out var x) ? x : 0;
                int cv = i < c.Length && int.TryParse(new string(c[i].TakeWhile(char.IsDigit).ToArray()), out var y) ? y : 0;
                if (lv != cv) return lv > cv;
            }
            return false;
        }
        catch { return false; }
    }

    // Скачивание установщика из релиза во временную папку с прогрессом 0..100.
    public async Task<(bool Ok, string Path, string Error)> DownloadSetupAsync(
        string assetUrl, string assetName, IProgress<double>? progress = null)
    {
        var dest = System.IO.Path.Combine(System.IO.Path.GetTempPath(), assetName);
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, assetUrl);
            req.Headers.UserAgent.ParseAdd("SystemGuard-Updater");
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            resp.EnsureSuccessStatusCode();
            long? total = resp.Content.Headers.ContentLength;
            using var stream = await resp.Content.ReadAsStreamAsync();
            using var fs = new System.IO.FileStream(dest, System.IO.FileMode.Create, System.IO.FileAccess.Write);
            var buffer = new byte[128 * 1024];
            long received = 0;
            int read;
            while ((read = await stream.ReadAsync(buffer)) > 0)
            {
                await fs.WriteAsync(buffer.AsMemory(0, read));
                received += read;
                if (total > 0)
                {
                    try { progress?.Report(Math.Round(received * 100.0 / total.Value, 1)); } catch { }
                }
            }
            try { progress?.Report(100); } catch { }
            return (true, dest, "");
        }
        catch (Exception ex)
        {
            try { if (System.IO.File.Exists(dest)) System.IO.File.Delete(dest); } catch { }
            return (false, "", ex.Message);
        }
    }

    // Запуск установщика в тихом режиме и выход из приложения,
    // чтобы Inno Setup мог заменить файлы (CloseApplications помогает, но выход надёжнее).
    public static string LaunchSetupAndExit(string setupPath)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(setupPath, "/SILENT")
            {
                UseShellExecute = true
            });
            try
            {
                if (Avalonia.Application.Current?.ApplicationLifetime
                    is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime life)
                    life.Shutdown();
                else
                    Environment.Exit(0);
            }
            catch { Environment.Exit(0); }
            return "Installing update… the app will restart.";
        }
        catch (Exception ex) { return $"Cannot start installer: {ex.Message}"; }
    }
}
