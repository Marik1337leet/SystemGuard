using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace SystemGuard.Desktop.Services;

// Уведомления в Slack через Incoming Webhook. Реальный HTTP POST.
public static class SlackNotifierService
{
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public static async Task<string> SendAsync(string webhookUrl, string text)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(webhookUrl)) return "Empty webhook URL";
            var payload = JsonSerializer.Serialize(new { text });
            using var res = await _http.PostAsync(webhookUrl.Trim(),
                new StringContent(payload, Encoding.UTF8, "application/json"));
            return res.IsSuccessStatusCode ? "Sent to Slack" : $"Slack error: {(int)res.StatusCode}";
        }
        catch (Exception ex) { return $"Slack failed: {ex.Message}"; }
    }
}
