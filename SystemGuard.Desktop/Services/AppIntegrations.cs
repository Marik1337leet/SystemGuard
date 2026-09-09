using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace SystemGuard.Desktop.Services;

// Инвентаризация железа и ПО (для корпоративных отчётов) + экспорт CSV/JSON.
public class InstalledAppInfo
{
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string Publisher { get; set; } = "";
    public string InstallDate { get; set; } = "";
}

public static class InventoryService
{
    public static List<InstalledAppInfo> GetInstalledApps(int max = 2000)
    {
        var list = new List<InstalledAppInfo>();
        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
            foreach (var sub in new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" })
            try
            {
                using var key = hive.OpenSubKey(sub);
                if (key == null) continue;
                foreach (var name in key.GetSubKeyNames())
                {
                    try
                    {
                        using var k = key.OpenSubKey(name);
                        var display = k?.GetValue("DisplayName")?.ToString();
                        if (string.IsNullOrWhiteSpace(display)) continue;
                        if ((k?.GetValue("SystemComponent")?.ToString() ?? "0") == "1") continue;
                        list.Add(new InstalledAppInfo
                        {
                            Name = display.Trim(),
                            Version = k?.GetValue("DisplayVersion")?.ToString() ?? "",
                            Publisher = k?.GetValue("Publisher")?.ToString() ?? "",
                            InstallDate = k?.GetValue("InstallDate")?.ToString() ?? ""
                        });
                        if (list.Count >= max) return list;
                    }
                    catch { }
                }
            }
            catch { }
        return list.GroupBy(a => a.Name).Select(g => g.First()).OrderBy(a => a.Name).ToList();
    }

    public static string BuildReport()
    {
        var cpu = DetailedSystemInfoService.GetCpuDetails();
        var ram = DetailedSystemInfoService.GetRamDetails();
        var disks = DetailedSystemInfoService.GetSmartDisks();
        var apps = GetInstalledApps();
        var sb = new StringBuilder();
        sb.AppendLine($"Machine: {Environment.MachineName} | OS: {Environment.OSVersion} | 64-bit: {Environment.Is64BitOperatingSystem}");
        sb.AppendLine($"CPU: {cpu.Name} {cpu.Cores}C/{cpu.LogicalProcessors}T");
        sb.AppendLine($"RAM: {ram.TotalGb}GB {ram.ChannelMode} @ {ram.SpeedMhz}MHz");
        foreach (var d in disks) sb.AppendLine($"Disk: {d.Model} {d.SizeGb}GB [{d.Health}]");
        sb.AppendLine($"Software: {apps.Count} apps");
        foreach (var a in apps.Take(200)) sb.AppendLine($"  {a.Name} {a.Version} ({a.Publisher})");
        return sb.ToString();
    }

    public static string ExportCsv(string path)
    {
        var sb = new StringBuilder("Name,Version,Publisher,InstallDate\n");
        foreach (var a in GetInstalledApps())
            sb.AppendLine($"\"{a.Name.Replace("\"", "'")}\",\"{a.Version}\",\"{a.Publisher.Replace("\"", "'")}\",\"{a.InstallDate}\"");
        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        return path;
    }

    public static string ExportJson(string path)
    {
        var report = new
        {
            Machine = Environment.MachineName,
            OS = Environment.OSVersion.ToString(),
            Cpu = DetailedSystemInfoService.GetCpuDetails().Name,
            Apps = GetInstalledApps()
        };
        File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        return path;
    }
}

// Универсальные вебхуки: Slack / IFTTT / Zapier / Home Assistant / свой сервер.
public static class WebhookNotifier
{
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public static async Task<string> PostJsonAsync(string url, object payload)
    {
        try
        {
            if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return "URL must start with http(s)";
            var json = JsonSerializer.Serialize(payload);
            using var res = await _http.PostAsync(url, new StringContent(json, Encoding.UTF8, "application/json"));
            return res.IsSuccessStatusCode ? "Delivered" : $"HTTP {(int)res.StatusCode}";
        }
        catch (Exception ex) { return $"Webhook failed: {ex.Message}"; }
    }

    public static Task<string> PostTextAsync(string url, string text) =>
        PostJsonAsync(url, new { text, source = "SystemGuard", at = DateTime.Now });

    public static Task<string> NotifyHomeAssistantAsync(string baseUrl, string token, string message) =>
        PostJsonAsync($"{baseUrl.TrimEnd('/')}/api/services/notify/notify",
            new { message });
}

// Email-отчёты планировщика через SMTP (без новых зависимостей).
public class SmtpOptions
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    public bool Ssl { get; set; } = true;
    public string User { get; set; } = "";
    public string Password { get; set; } = "";
    public string From { get; set; } = "";
    public string To { get; set; } = "";
}

public static class SmtpReportService
{
    public static async Task<string> SendAsync(SmtpOptions o, string subject, string body)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(o.Host) || string.IsNullOrWhiteSpace(o.To)) return "SMTP host/recipient not set";
            using var msg = new MailMessage(o.From.Length > 0 ? o.From : o.User, o.To, subject, body);
            using var client = new SmtpClient(o.Host, o.Port) { EnableSsl = o.Ssl };
            if (!string.IsNullOrEmpty(o.User))
                client.Credentials = new System.Net.NetworkCredential(o.User, o.Password);
            await client.SendMailAsync(msg);
            return "Email sent";
        }
        catch (Exception ex) { return $"Email failed: {ex.Message}"; }
    }
}

// Защита приложения паролем: SHA256-хэш с солью в LocalAppData.
public static class PasswordLockService
{
    private static string LockPath
    {
        get
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SystemGuard");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "app.lock");
        }
    }

    public static bool IsLockSet() => File.Exists(LockPath);

    public static string SetPassword(string password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < 4) return "Password must be ≥4 chars";
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(Convert.ToHexString(salt) + password));
        File.WriteAllText(LockPath, $"{Convert.ToHexString(salt)}:{Convert.ToHexString(hash)}");
        return "Password set";
    }

    public static bool Verify(string password)
    {
        try
        {
            var parts = File.ReadAllText(LockPath).Split(':');
            if (parts.Length != 2) return false;
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(parts[0] + password));
            return CryptographicOperations.FixedTimeEquals(hash, Convert.FromHexString(parts[1]));
        }
        catch { return false; }
    }

    public static void RemoveLock()
    {
        try { File.Delete(LockPath); } catch { }
    }
}
