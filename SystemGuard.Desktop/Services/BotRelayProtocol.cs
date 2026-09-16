// Надёжный транспорт "вне дома" без туннеля: команды идут обычным текстом
// через Telegram (Bot API sendMessage с токеном бота, WebApp sendData,
// копипаста команды в чат), ПК-бот их исполняет и отвечает в тот же чат.
// Работает из любой сети (исходящий HTTPS к api.telegram.org, без проброса
// портов, без белых IP, без туннеля). Задержка 1-3с — для команд хватает.
// Live-туннель остаётся ускорителем для видео/экрана внутри WebApp.
//
// Формат запроса от телефона (обычное сообщение боту):
//   🤖SG:{"id":"<uuid>","action":"status","arg":""}
// Ответ — ОБЫЧНЫЙ человекочитаемый текст в чат (без машинных пакетов:
// бот не видит собственные сообщения через getUpdates, парсить ответ
// в приложении технически невозможно — человек читает его в чате).
using System;
using System.Text.Json;

namespace SystemGuard.Desktop.Services;

/// <summary>Машинный протокол Android ↔ ПК через Telegram-чат. Без зависимостей.</summary>
public static class BotRelayProtocol
{
    public const string RequestPrefix = "🤖SG:";

    private static readonly JsonSerializerOptions Opts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public sealed record RelayRequest(string Id, string Action, string Arg);

    public static bool IsRelayRequest(string? text) =>
        !string.IsNullOrEmpty(text) && text.StartsWith(RequestPrefix, StringComparison.Ordinal);

    public static string BuildRequest(string action, string arg = "")
    {
        var req = new RelayRequest(Guid.NewGuid().ToString("N"), (action ?? "").Trim(), (arg ?? "").Trim());
        return RequestPrefix + JsonSerializer.Serialize(req, Opts);
    }

    /// <summary>Детерминированный build (тот же id) — нужен тестам и ретраям.</summary>
    public static string BuildRequestWithId(string id, string action, string arg = "")
    {
        var req = new RelayRequest((id ?? "").Trim(), (action ?? "").Trim(), (arg ?? "").Trim());
        return RequestPrefix + JsonSerializer.Serialize(req, Opts);
    }

    public static RelayRequest? TryParseRequest(string? text)
    {
        if (!IsRelayRequest(text)) return null;
        try
        {
            var json = text![RequestPrefix.Length..].Trim();
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            var id = r.TryGetProperty("id", out var i) ? i.GetString() ?? "" : "";
            var action = r.TryGetProperty("action", out var a) ? a.GetString() ?? "" : "";
            string arg = "";
            if (r.TryGetProperty("arg", out var g))
                arg = g.ValueKind == JsonValueKind.String ? g.GetString() ?? "" : g.GetRawText();
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(action)) return null;
            // Защита от мусора: id — 8..64 символа, action — буква/цифра/_/-, arg — до 8К.
            id = id.Trim();
            action = action.Trim().TrimStart('/').ToLowerInvariant();
            if (id.Length is < 8 or > 64 || action.Length is 0 or > 64) return null;
            if (arg.Length > 8192) arg = arg[..8192];
            return new RelayRequest(id, action, arg.Trim());
        }
        catch { return null; }
    }

    /// <summary>
    /// Человекочитаемый ответ в чат (лимит Telegram 4096 символов).
    /// Машинных пакетов нет осознанно: собственные сообщения бот через
    /// getUpdates не видит, парсить ответ в приложении невозможно.
    /// </summary>
    public static string FormatReply(string action, bool ok, string message, string output = "")
    {
        message = (message ?? "").Trim();
        output = (output ?? "").Trim();
        if (message.Length == 0) message = ok ? "OK" : "Error";
        string text = ok ? message : "Error: " + message;
        if (ok && output.Length > 0 && !string.Equals(output, message, StringComparison.Ordinal))
            text += "\n" + output;
        if (text.Length > 4000) text = text[..4000] + "…";
        return text;
    }

    /// <summary>
    /// Какие relay-действия разрешены не-админу (только чтение, без управления).
    /// Админу разрешено всё, что умеет RemoteActions.
    /// </summary>
    public static bool IsReadOnlyAction(string action) => action switch
    {
        "status" or "perf" or "sysinfo" or "battery" or "free" or "uptime"
            or "ip" or "processes" or "procs" or "apps" or "netstat" or "connections"
            or "wifi" or "startup" or "power_plans" or "sched_list" or "eventlog"
            or "services" or "license_status" or "policy" or "privacy" or "terms"
            or "safety" or "cleanup_info" or "uninstall_list" or "defender_status"
            or "remote" or "rdp" or "ping" => true,
        _ => false,
    };
}
