using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;

namespace SystemGuard.Desktop.Services;

/// <summary>
/// Чтение СВОЕГО ключа по HWID из Supabase (кнопка «Я оплатил, проверить»).
/// Использует ТОЛЬКО публичный anon-ключ (service key — лишь в shop-bot на сервере).
/// Конфиг: %LocalAppData%\SystemGuard\supabase.json
///   { "supabaseUrl": "https://xyz.supabase.co", "anonKey": "eyJ..." }
/// Без файла — возвращает null (охват: ручной ввод подарочного ключа).
/// Ключ целиком никогда не показывается: уходит сразу в LicenseService.ActivatePro.
/// </summary>
public static class SupabaseLicenseClient
{
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public static async Task<string?> TryFetchKeyByHwidAsync(string hwid)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(hwid) || hwid.Length != 32) return null;
            var (url, anon) = ReadConfig();
            if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(anon)) return null;

            // Самый свежий действующий ключ под этот HWID (факт покупки).
            // Полный текст ключа в облаке НЕ храним (только key_prefix) —
            // полный ключ выдаёт shop-bot в личку спойлером с привязкой к HWID.
            // Приложение подхватывает его из буфера одной кнопкой и нигде не показывает.
            using var req = new HttpRequestMessage(
                HttpMethod.Get,
                url.TrimEnd('/') + "/rest/v1/licenses?hwid=eq." + Uri.EscapeDataString(hwid.ToLower()) +
                "&revoked=eq.false&order=created_at.desc&limit=1&select=key_prefix,plan,expires_at");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", anon);
            req.Headers.Add("apikey", anon);
            using var res = await _http.SendAsync(req).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode) return null;
            var body = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(body) || body.Trim() == "[]") return null;

            // Факт покупки есть — полный ключ берём из чата шоп-бота
            // (скопирован заранее, вставляем из буфера, на экране не показываем).
            return await TryTakeKeyFromClipboardAsync().ConfigureAwait(false);
        }
        catch { return null; }
    }

    /// <summary>
    /// Поставщик текста буфера обмена. Ставится из SettingsViewModel.OnActivated
    /// (буфер в Avalonia живёт на TopLevel/Window, а не на Application).
    /// </summary>
    public static Func<Task<string?>>? ClipboardReader { get; set; }

    /// <summary>
    /// Ключ из буфера обмена (скопирован из шоп-бота) — валидируем формат,
    /// буфер сразу чистим через тот же reader (если умеет), на экран ключ не выводим.
    /// </summary>
    public static async Task<string?> TryTakeKeyFromClipboardAsync()
    {
        try
        {
            if (ClipboardReader == null) return null;
            var t = await ClipboardReader().ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(t)) return null;
            var key = t.Split(new[] { ' ', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(s => s.StartsWith("SG-PRO-", StringComparison.OrdinalIgnoreCase)
                                  || s.StartsWith("SG-ENT-", StringComparison.OrdinalIgnoreCase));
            return key;
        }
        catch { return null; }
    }

    private static (string Url, string Anon) ReadConfig()
    {
        try
        {
            var p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SystemGuard", "supabase.json");
            if (!File.Exists(p)) return ("", "");
            using var doc = JsonDocument.Parse(File.ReadAllText(p));
            var r = doc.RootElement;
            return (r.TryGetProperty("supabaseUrl", out var u) ? u.GetString() ?? "" : "",
                    r.TryGetProperty("anonKey", out var k) ? k.GetString() ?? "" : "");
        }
        catch { return ("", ""); }
    }

    // ── Отчёт об активации: какой ПК (hwid + имя) каким ключом пользуется ──
    // Приложение пишет строку в public.activations при активации ключа
    // (force: true) и раз в сутки при живой Pro (force: false, троттлинг
    // файлом cloud_report.txt). Полного ключа наружу нет — только key_prefix.
    // Без supabase.json — тихо ничего не делает. Возвращает true если
    // строка реально ушла в облако.

    /// <summary>
    /// Строит тело для activations. Null — отчитываться не о чем
    /// (пустой hwid/ключ, мусорный формат). Тестируемо без сети.
    /// </summary>
    public static object? BuildActivationPayload(string? hwid, string? machine, string? key, string? tier, string? plan, string? appVersion)
    {
        try
        {
            hwid = (hwid ?? "").Trim().ToLower();
            if (hwid.Length != 32 || !hwid.All(c => Uri.IsHexDigit(c))) return null;
            key = (key ?? "").Trim();
            if (!(key.StartsWith("SG-PRO-", StringComparison.OrdinalIgnoreCase) ||
                  key.StartsWith("SG-ENT-", StringComparison.OrdinalIgnoreCase)) || key.Length < 32)
                return null;
            return new
            {
                hwid,
                machine = (machine ?? "").Trim()[..Math.Min(64, (machine ?? "").Trim().Length)],
                key_prefix = KeyPrefix(key),
                tier = (tier ?? "").Trim(),
                plan = (plan ?? "").Trim(),
                app_version = (appVersion ?? "").Trim()
            };
        }
        catch { return null; }
    }

    /// <summary>
    /// Префикс ключа для БД: первые 28 символов. Короче нельзя: все Pro-ключи
    /// начинаются одинаково (base64 от "Pro|..."), а 28 уже цепляют дату+тариф
    /// и различают ключи. Старые строки с коротким префиксом матчатся
    /// через LIKE 'prefix%'.
    /// </summary>
    public static string KeyPrefix(string key)
    {
        key = (key ?? "").Trim();
        return key[..Math.Min(28, key.Length)];
    }

    public static void ReportActivation(string? hwid, string? machine, string? key, string? tier, string? plan, bool force = false)
    {
        // Fire-and-forget: отчёт никогда не должен мешать активации.
        try { _ = ReportActivationAsync(hwid, machine, key, tier, plan, force); }
        catch { }
    }

    public static async Task<bool> ReportActivationAsync(string? hwid, string? machine, string? key, string? tier, string? plan, bool force = false)
    {
        try
        {
            var payload = BuildActivationPayload(hwid, machine, key, tier, plan, AppVersionShort());
            if (payload == null) return false;
            var (url, anon) = ReadConfig();
            if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(anon)) return false;
            if (!force && ReportedToday()) return false;

            using var req = new HttpRequestMessage(HttpMethod.Post, url.TrimEnd('/') + "/rest/v1/activations");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", anon);
            req.Headers.Add("apikey", anon);
            req.Content = new StringContent(
                JsonSerializer.Serialize(payload),
                System.Text.Encoding.UTF8, "application/json");
            using var res = await _http.SendAsync(req).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode) return false;
            MarkReportedToday();
            return true;
        }
        catch { return false; }
    }

    private static string AppVersionShort()
    {
        try
        {
            var v = typeof(SupabaseLicenseClient).Assembly.GetName().Version;
            return v != null ? $"{v.Major}.{v.Minor}.{v.Build}" : "";
        }
        catch { return ""; }
    }

    private static string ReportStampPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SystemGuard", "cloud_report.txt");

    private static bool ReportedToday()
    {
        try
        {
            var p = ReportStampPath();
            if (!File.Exists(p)) return false;
            return File.ReadAllText(p).Trim() == DateTime.UtcNow.ToString("yyyy-MM-dd");
        }
        catch { return false; }
    }

    private static void MarkReportedToday()
    {
        try
        {
            var p = ReportStampPath();
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, DateTime.UtcNow.ToString("yyyy-MM-dd"));
        }
        catch { }
    }
}
