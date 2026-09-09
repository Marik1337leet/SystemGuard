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
}
