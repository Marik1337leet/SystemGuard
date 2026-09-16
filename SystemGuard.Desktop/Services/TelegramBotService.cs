using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.Payments;
using Telegram.Bot.Types.ReplyMarkups;
using File = System.IO.File;

namespace SystemGuard.Desktop.Services;

// Telegram-бот v3.
//   • polling-цикл отделён от исполнения команд (тяжёлая команда не вешает бота);
//   • отправка через шлюз: rate-limit + уважение к FloodWait (429 RetryAfter);
//   • длинные выводы режутся на чанки, пользовательский текст HTML-экранируется;
//   • команды понимают аргумент в той же строке: "/ls C:\" "/cmd ipconfig";
//   • громкость — winmm (мгновенно и точно), яркость — CIM с честной ошибкой;
//   • камера — FlashCap (DirectShow/MediaFoundation), скриншот — общий сервис;
//   • Stars-счета с пустым providerToken (иначе PAYMENT_PROVIDER_INVALID);
//   • /live — ссылка на живой WebApp-доступ (Cloudflare Tunnel).
public sealed class TelegramBotService : IDisposable
{
    private string _token = "", _chatId = "";
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(60) };
    private TelegramBotClient? _botClient;
    private CancellationTokenSource? _cts;
    private Task? _listenTask, _scheduleTask, _notifyTask;
    private int _listening;
    private bool _disposed;
    private bool _notifyEnabled;

    private readonly List<long> _authorizedUsers = new();
    private readonly List<long> _adminUsers = new();
    private readonly List<BotTask> _scheduledTasks = new();
    private readonly List<string> _actionLog = new();
    private readonly Dictionary<long, Func<string, Task>> _pendingActions = new();
    private readonly object _stateLock = new();

    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private DateTime _lastSendUtc = DateTime.MinValue;
    private static readonly TimeSpan MinSendInterval = TimeSpan.FromMilliseconds(150);

    public bool IsConfigured => !string.IsNullOrEmpty(_token) && !string.IsNullOrEmpty(_chatId);
    public bool IsListening => Volatile.Read(ref _listening) == 1;
    public IReadOnlyList<string> ActionLog { get { lock (_stateLock) return _actionLog.ToList(); } }

    public void Configure(string token, string chatId)
    {
        token = (token ?? "").Trim();
        if (string.IsNullOrWhiteSpace(token))
            throw new ArgumentException("Bot token is empty", nameof(token));
        if (!long.TryParse((chatId ?? "").Trim(), out var id))
            throw new ArgumentException("Chat ID must be numeric (see @userinfobot)", nameof(chatId));
        _token = token;
        _chatId = id.ToString();
        _botClient = new TelegramBotClient(token);
        lock (_stateLock)
        {
            if (!_authorizedUsers.Contains(id)) _authorizedUsers.Add(id);
            if (!_adminUsers.Contains(id)) _adminUsers.Add(id);
        }
    }

    public void AddUser(long chatId, bool admin)
    {
        lock (_stateLock)
        {
            if (!_authorizedUsers.Contains(chatId)) _authorizedUsers.Add(chatId);
            if (admin && !_adminUsers.Contains(chatId)) _adminUsers.Add(chatId);
            if (!admin) _adminUsers.Remove(chatId);
        }
    }

    public void RemoveUser(long chatId)
    {
        lock (_stateLock)
        {
            _authorizedUsers.Remove(chatId);
            _adminUsers.Remove(chatId);
        }
    }

    public void Disconnect()
    {
        StopListening();
        lock (_stateLock)
        {
            _pendingActions.Clear();
            _scheduledTasks.Clear();
        }
    }

    public void StartListening()
    {
        if (_botClient == null || _disposed) return;
        if (Interlocked.Exchange(ref _listening, 1) == 1) return;
        StopTasks();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _listenTask = Task.Run(() => ListenAsync(ct), ct);
        _scheduleTask = Task.Run(() => ScheduleChecker(ct), ct);
        _notifyTask = Task.Run(() => NotifyChecker(ct), ct);
    }

    public void StopListening()
    {
        if (Interlocked.Exchange(ref _listening, 0) == 0) { StopTasks(); return; }
        StopTasks();
        foreach (var id in _streams.Keys.ToList()) StopScreenStream(id);
    }

    private void StopTasks()
    {
        try { _cts?.Cancel(); } catch { }
        _listenTask = _scheduleTask = _notifyTask = null;
        try { _cts?.Dispose(); } catch { }
        _cts = null;
    }

    private bool IsAuthorized(long uid) { lock (_stateLock) return _authorizedUsers.Contains(uid); }
    private bool IsAdmin(long uid) { lock (_stateLock) return _adminUsers.Contains(uid); }

    private void Log(string a, long u)
    {
        lock (_stateLock)
        {
            _actionLog.Insert(0, $"[{DateTime.Now:HH:mm}] U{u}: {a}");
            while (_actionLog.Count > 200) _actionLog.RemoveAt(_actionLog.Count - 1);
        }
    }

    private bool Ready() => _botClient != null && !_disposed;

    private async Task ListenAsync(CancellationToken ct)
    {
        int offset = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var updates = await _botClient!.GetUpdatesAsync(
                    offset: offset, limit: 50, timeout: 20,
                    allowedUpdates: new[] { UpdateType.Message, UpdateType.CallbackQuery, UpdateType.PreCheckoutQuery },
                    cancellationToken: ct).ConfigureAwait(false);

                foreach (var u in updates)
                {
                    offset = u.Id + 1;
                    _ = Task.Run(() => HandleUpdateSafe(u), ct);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (ApiRequestException ex) when (ex.ErrorCode == 409)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
            catch
            {
                try { await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task HandleUpdateSafe(Update u)
    {
        try { await HandleUpdate(u).ConfigureAwait(false); }
        catch { }
    }

    private async Task HandleUpdate(Update u)
    {
        if (u.CallbackQuery != null)
        {
            var cq = u.CallbackQuery;
            var cid = cq.Message?.Chat.Id ?? 0;
            if (cid != 0 && !IsAuthorized(cid))
            {
                try { await _botClient!.AnswerCallbackQueryAsync(cq.Id, "Unauthorized").ConfigureAwait(false); }
                catch { }
                return;
            }
            Log("callback:" + (cq.Data ?? "?"), cid);
            try
            {
                await HandleCallback(cq.Data, cid).ConfigureAwait(false);
                await _botClient!.AnswerCallbackQueryAsync(cq.Id).ConfigureAwait(false);
            }
            catch { }
            return;
        }

        if (u.PreCheckoutQuery != null)
        {
            try { await _botClient!.AnswerPreCheckoutQueryAsync(u.PreCheckoutQuery.Id).ConfigureAwait(false); }
            catch { }
            return;
        }

        var msg = u.Message;
        if (msg == null) return;
        var cid2 = msg.Chat.Id;

        if (msg.WebAppData != null)
        {
            if (!IsAuthorized(cid2))
            {
                await SendHtml(cid2, "<b>Unauthorized.</b> Ask the PC owner to add your chat ID.").ConfigureAwait(false);
                return;
            }
            Log("webapp:" + Trim(msg.WebAppData.Data, 120), cid2);
            await HandleWebAppData(msg.WebAppData.Data, cid2).ConfigureAwait(false);
            return;
        }

        if (msg.SuccessfulPayment != null)
        {
            try { await OnSuccessfulPayment(msg).ConfigureAwait(false); } catch { }
            return;
        }

        if (msg.Document != null || (msg.Photo != null && msg.Photo.Length > 0))
        {
            if (!IsAuthorized(cid2)) return;
            await HandleFileUpload(msg).ConfigureAwait(false);
            return;
        }

        if (!string.IsNullOrWhiteSpace(msg.Text))
        {
            if (!IsAuthorized(cid2))
            {
                await SendHtml(cid2, "<b>Unauthorized.</b>\nYour chat ID: <code>" + cid2 + "</code>").ConfigureAwait(false);
                return;
            }
            // Машинный relay от Android-приложения (работает вне дома без туннеля).
            // Обычные пользователи чата эти пакеты не шлют — ветка им не мешает.
            if (BotRelayProtocol.IsRelayRequest(msg.Text))
            {
                Log("relay:" + Trim(msg.Text, 120), cid2);
                await HandleRelayRequest(msg.Text, cid2).ConfigureAwait(false);
                return;
            }
            Log(Trim(msg.Text, 120), cid2);
            await HandleCommand(msg.Text, cid2).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Чистый мэппинг WebApp→команда (без бота): sendData из WebApp, кнопки
    /// и копипаста из чата. Покрыт тестами; HandleWebAppData — тонкая
    /// обвязка поверх него.
    /// </summary>
    public static bool TryParseWebAppData(string? data, out string action, out string arg)
    {
        action = ""; arg = "";
        if (string.IsNullOrWhiteSpace(data)) return false;
        try
        {
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("action", out var a)) action = a.GetString() ?? "";
                if (root.TryGetProperty("arg", out var g))
                    arg = g.ValueKind == JsonValueKind.String ? g.GetString() ?? "" : g.ToString();
                if (root.TryGetProperty("value", out var v) && string.IsNullOrEmpty(arg))
                    arg = v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString();
            }
            else action = data;
        }
        catch { action = data; }

        action = action.Trim().TrimStart('/').ToLowerInvariant();
        if (action is "dashboard" or "info" or "power") action = "status";
        if (action == "pause") action = "play";
        arg = (arg ?? "").Trim();
        if (arg.Length > 8192) arg = arg[..8192];
        return action.Length > 0;
    }

    private async Task HandleWebAppData(string data, long cid)
    {
        if (!TryParseWebAppData(data, out var action, out var arg)) return;
        await HandleCommand("/" + action + (string.IsNullOrWhiteSpace(arg) ? "" : " " + arg.Trim()), cid)
            .ConfigureAwait(false);
    }

    // ── Bot Relay: Android ↔ ПК через Telegram (вне дома, без туннеля) ─────
    // Телефон шлёт 🤖SG:{...}, ПК исполняет через тот же RemoteActions и отвечает
    // ОБЫЧНЫМ текстом в тот же чат (машинных пакетов нет: свои сообщения бот
    // через getUpdates не видит, парсить ответ в приложении невозможно —
    // человек читает его в чате). Скриншоты/камера идут обычной фоткой
    // с подписью [relay:id].
    private async Task HandleRelayRequest(string text, long cid)
    {
        var req = BotRelayProtocol.TryParseRequest(text);
        if (req == null) return; // битый пакет — молча игнорируем, не спамим чат
        var admin = IsAdmin(cid);
        if (!admin && !BotRelayProtocol.IsReadOnlyAction(req.Action))
        {
            await SendRelayText(cid, BotRelayProtocol.FormatReply(req.Action, false, "Admins only")).ConfigureAwait(false);
            return;
        }
        try
        {
            // Скрин/камера: сначала текст, потом фото (фото может не влезть,
            // а текстовый ответ уже в чате).
            if (req.Action is "screenshot" or "shot")
            {
                await SendRelayText(cid, "Uploading screenshot…").ConfigureAwait(false);
                await SendScreenshotToChat(cid, "[relay:" + req.Id + "]").ConfigureAwait(false);
                return;
            }
            if (req.Action is "cam" or "camera")
            {
                await SendRelayText(cid, "Uploading camera frame…").ConfigureAwait(false);
                await SendWebcamToChat(cid, "[relay:" + req.Id + "]").ConfigureAwait(false);
                return;
            }
            var result = await RemoteActions.ExecuteAsync(req.Action, req.Arg).ConfigureAwait(false);
            var (ok, message, output) = FlattenResult(result);
            await SendRelayText(cid, BotRelayProtocol.FormatReply(req.Action, ok, message, output)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await SendRelayText(cid, BotRelayProtocol.FormatReply(req.Action, false, Trim(ex.Message, 200))).ConfigureAwait(false);
        }
    }

    private static (bool Ok, string Message, string Output) FlattenResult(object result)
    {
        try
        {
            var json = JsonSerializer.Serialize(result);
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            bool ok = !r.TryGetProperty("ok", out var o) || o.ValueKind == JsonValueKind.True;
            string Str(string name)
            {
                if (!r.TryGetProperty(name, out var v)) return "";
                return v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.GetRawText();
            }
            var message = Str("message");
            var output = Str("output");
            if (string.IsNullOrEmpty(message) && !string.IsNullOrEmpty(output)) message = output;
            if (string.IsNullOrEmpty(message)) message = ok ? "OK" : "Error";
            return (ok, message, output);
        }
        catch
        {
            return (true, result?.ToString() ?? "OK", "");
        }
    }

    // Plain text без HTML-парсинга: внутри выводов команд скобки/уголки,
    // парсер их ломает. Человек читает ответ в чате.
    private Task SendRelayText(long cid, string text)
    {
        return ThrottledSend(cid, (chat, ct) =>
            _botClient!.SendTextMessageAsync(chat, text, cancellationToken: ct));
    }

    /// <summary>
    /// Автопуш свежей live-ссылки владельцу (после Publish и каждого реконнекта
    /// туннеля). Иначе вне дома телефон хранит мёртвый URL и "ничего не работает".
    /// Вызывается из UI при смене LiveServices.Tunnel.PublicUrl.
    /// </summary>
    public Task PushLiveUrlAsync(string url, string provider)
    {
        try
        {
            if (!Ready() || !IsConfigured) return Task.CompletedTask;
            if (!long.TryParse(_chatId, out var id)) return Task.CompletedTask;
            var html = "<b>Live доступ обновлён.</b>\n\n" +
                $"Транспорт: <code>{Esc(provider)}</code>\n" +
                $"Ссылка: <code>{Esc(url)}</code>\n" +
                $"Токен: <code>{Esc(LiveServices.Token)}</code>\n\n" +
                "В приложении: Статус → вставить ссылку + токен → Связать.\n" +
                "<i>Старая ссылка больше не работает — туннель её сменил сам.</i>";
            return SendHtml(id, html);
        }
        catch { return Task.CompletedTask; }
    }

    public static string ExtractCommand(string text)
    {
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
            if (part.StartsWith("/"))
            {
                var c = part.ToLower().Trim();
                var at = c.IndexOf('@');
                return at > 0 ? c[..at] : c;
            }
        return text.ToLower().Trim();
    }

    public static string ExtractArg(string text)
    {
        var i = text.IndexOf(' ');
        return i < 0 ? "" : text[(i + 1)..].Trim().Trim('"');
    }

    private async Task HandleCommand(string rawText, long cid)
    {
        var text = (rawText ?? "").Trim();
        if (text.Length == 0) return;

        Func<string, Task>? pending;
        lock (_stateLock) _pendingActions.TryGetValue(cid, out pending);
        if (pending != null)
        {
            if (text.Equals("cancel", StringComparison.OrdinalIgnoreCase)
                || text.Equals("отмена", StringComparison.OrdinalIgnoreCase))
            {
                lock (_stateLock) _pendingActions.Remove(cid);
                await SendMainMenu(cid).ConfigureAwait(false);
                return;
            }
            lock (_stateLock) _pendingActions.Remove(cid);
            try { await pending(text).ConfigureAwait(false); }
            catch (Exception ex) { await SendHtml(cid, "Error: " + Esc(Trim(ex.Message, 300))).ConfigureAwait(false); }
            await Task.Delay(250).ConfigureAwait(false);
            await SendMainMenu(cid).ConfigureAwait(false);
            return;
        }

        var cmd = ExtractCommand(text);
        var arg = ExtractArg(text);
        cmd = MapButtonToCommand(cmd) ?? cmd;

        switch (cmd)
        {
            case "/start": await SendWelcome(cid).ConfigureAwait(false); break;
            case "/help": case "/menu": await SendHelp(cid).ConfigureAwait(false); break;
            case "/live": await SendLiveCard(cid).ConfigureAwait(false); break;
            case "/status": await SendStatusCard(cid).ConfigureAwait(false); break;
            case "/apps": await SendRunningApps(cid).ConfigureAwait(false); break;
            case "/processes":
                await SendHtml(cid, "<i>Collecting process list…</i>").ConfigureAwait(false);
                await SendMsg(cid, await Exec("tasklist /fo table").ConfigureAwait(false)).ConfigureAwait(false);
                break;
            case "/shutdown":
                if (!await RequireAdmin(cid).ConfigureAwait(false)) break;
                await Exec("shutdown /s /t 60").ConfigureAwait(false);
                await SendHtml(cid, "<b>Shutdown in 60 seconds.</b>\n/cancel — abort").ConfigureAwait(false);
                break;
            case "/restart":
                if (!await RequireAdmin(cid).ConfigureAwait(false)) break;
                await Exec("shutdown /r /t 60").ConfigureAwait(false);
                await SendHtml(cid, "<b>Restart in 60 seconds.</b>\n/cancel — abort").ConfigureAwait(false);
                break;
            case "/cancel":
                if (!await RequireAdmin(cid).ConfigureAwait(false)) break;
                await Exec("shutdown /a").ConfigureAwait(false);
                await SendHtml(cid, "Shutdown timer cancelled.").ConfigureAwait(false);
                break;
            case "/sleep":
                await Exec("rundll32.exe powrprof.dll,SetSuspendState 0,1,0").ConfigureAwait(false);
                await SendHtml(cid, "PC is going to <b>sleep</b>.").ConfigureAwait(false);
                break;
            case "/lock":
                await Exec("rundll32.exe user32.dll,LockWorkStation").ConfigureAwait(false);
                await SendHtml(cid, "Workstation <b>locked</b>.").ConfigureAwait(false);
                break;
            case "/screenshot": await SendScreenshotToChat(cid).ConfigureAwait(false); break;
            case "/stream":
                // Фото-спам в чат убран: видео живёт внутри WebApp (до 60 FPS, весь экран).
                // Одиночные кадры — /screenshot и /cam.
                await SendStreamHint(cid).ConfigureAwait(false);
                break;
            case "/stop":
                StopScreenStream(cid);
                await SendHtml(cid, "Chat photo-stream is off. Video lives in the <b>Web App → Экран</b> tab.").ConfigureAwait(false);
                break;
            case "/cam": await SendWebcamToChat(cid).ConfigureAwait(false); break;
            case "/mute":
                await Task.Run(() => VolumeService.MuteToggle()).ConfigureAwait(false);
                await Task.Delay(150).ConfigureAwait(false);
                await SendHtml(cid, VolumeService.IsMuted()
                    ? "Sound <b>muted</b>."
                    : $"Sound on. Volume: <b>{VolumeService.GetPercent()}%</b>").ConfigureAwait(false);
                break;

            case "/ls":
                if (!string.IsNullOrWhiteSpace(arg)) { await ListDir(cid, arg).ConfigureAwait(false); break; }
                SetPending(cid, async v => await ListDir(cid, v).ConfigureAwait(false));
                await Ask(cid, "<b>Enter folder path</b> (empty = Desktop):").ConfigureAwait(false);
                break;
            case "/get":
                if (!string.IsNullOrWhiteSpace(arg)) { await SendFileToChat(cid, arg).ConfigureAwait(false); break; }
                SetPending(cid, async v => await SendFileToChat(cid, v).ConfigureAwait(false));
                await Ask(cid, "<b>Enter file path on PC:</b>").ConfigureAwait(false);
                break;
            case "/clean":
                if (!await RequireAdmin(cid).ConfigureAwait(false)) break;
                await SendHtml(cid, "<i>Cleaning temp files…</i>").ConfigureAwait(false);
                var r = await Exec("del /q /f %temp%\\* 2>nul & echo Done").ConfigureAwait(false);
                await SendHtml(cid, "<b>Cleanup:</b> <code>" + Esc(Trim(r, 500)) + "</code>").ConfigureAwait(false);
                break;
            case "/optimize_ram": case "/ram":
                await Exec("powershell [GC]::Collect()").ConfigureAwait(false);
                await SendHtml(cid, "RAM trim requested.").ConfigureAwait(false);
                break;
            case "/play": case "/pause":
                await VolumeService.MediaAsync(VolumeService.VK_MEDIA_PLAY_PAUSE).ConfigureAwait(false);
                await SendHtml(cid, "Play/Pause sent.").ConfigureAwait(false);
                break;
            case "/next":
                await VolumeService.MediaAsync(VolumeService.VK_MEDIA_NEXT).ConfigureAwait(false);
                await SendHtml(cid, "Next track sent.").ConfigureAwait(false);
                break;
            case "/prev":
                await VolumeService.MediaAsync(VolumeService.VK_MEDIA_PREV).ConfigureAwait(false);
                await SendHtml(cid, "Previous track sent.").ConfigureAwait(false);
                break;
            case "/license":
                switch (arg.Trim().ToLowerInvariant())
                {
                    case "monthly": await SendInvoice(cid, "Monthly", 300, 1).ConfigureAwait(false); break;
                    case "halfyear": case "half-year": await SendInvoice(cid, "Half-Year", 1000, 6).ConfigureAwait(false); break;
                    case "yearly": await SendInvoice(cid, "Yearly", 1800, 12).ConfigureAwait(false); break;
                    case "lifetime": await SendInvoice(cid, "Lifetime", 3000, 999).ConfigureAwait(false); break;
                    default: await ShowLicenseMenu(cid).ConfigureAwait(false); break;
                }
                break;
            // Алиасы для релиза: пользователь из рекламы ищет /buy и /tariffs.
            case "/buy": await ShowLicenseMenu(cid).ConfigureAwait(false); break;
            case "/tariffs": case "/tariff": case "/prices": case "/pro":
                await SendTariffs(cid).ConfigureAwait(false); break;
            case "/trial":
                await SendHtml(cid,
                    "<b>Free trial — 14 дней Pro</b>\n\n" +
                    "Триал включается в самом приложении: <b>Settings → License → Start 14-day trial</b>.\n" +
                    "Привязывается к этому ПК, второй раз взять нельзя.").ConfigureAwait(false);
                break;
            case "/files":
                await SendHtml(cid,
                    "<b>Files</b>\n• <code>/ls C:\\</code> — list folder\n• <code>/get C:\\file.txt</code> — download from PC\n• Send any file/photo — it lands in <b>Desktop\\TG Downloads</b>").ConfigureAwait(false);
                break;

            case "/volume":
                if (!string.IsNullOrWhiteSpace(arg)) { await SetVolume(cid, arg).ConfigureAwait(false); break; }
                SetPending(cid, async v => await SetVolume(cid, v).ConfigureAwait(false));
                if (!Ready()) return;
                await _botClient!.SendTextMessageAsync(cid, "Enter volume (0-100), 'up', 'down' or 'mute':",
                    replyMarkup: QuickKeyboard("80", "50", "20", "up", "down", "mute")).ConfigureAwait(false);
                break;

            case "/brightness":
                if (!string.IsNullOrWhiteSpace(arg)) { await SetBrightness(cid, arg).ConfigureAwait(false); break; }
                SetPending(cid, async v => await SetBrightness(cid, v).ConfigureAwait(false));
                if (!Ready()) return;
                await _botClient!.SendTextMessageAsync(cid, "Enter brightness (0-100):",
                    replyMarkup: QuickKeyboard("100", "70", "40")).ConfigureAwait(false);
                break;

            case "/open":
                if (!string.IsNullOrWhiteSpace(arg)) { await OpenApp(cid, arg).ConfigureAwait(false); break; }
                SetPending(cid, async v => await OpenApp(cid, v).ConfigureAwait(false));
                if (!Ready()) return;
                await _botClient!.SendTextMessageAsync(cid, "Enter app name:",
                    replyMarkup: QuickKeyboard("chrome", "steam", "notepad")).ConfigureAwait(false);
                break;

            case "/close":
                if (!await RequireAdmin(cid).ConfigureAwait(false)) break;
                if (!string.IsNullOrWhiteSpace(arg)) { await KillProcess(cid, arg).ConfigureAwait(false); break; }
                SetPending(cid, async v => await KillProcess(cid, v).ConfigureAwait(false));
                await Ask(cid, "<b>Enter process name to terminate:</b>").ConfigureAwait(false);
                break;

            case "/cmd":
                if (!await RequireAdmin(cid).ConfigureAwait(false)) break;
                if (!string.IsNullOrWhiteSpace(arg)) { await ExecuteAndSend(cid, arg).ConfigureAwait(false); break; }
                SetPending(cid, async v => await ExecuteAndSend(cid, v).ConfigureAwait(false));
                await Ask(cid, "<b>Enter CMD command:</b>").ConfigureAwait(false);
                break;

            case "/privacy": await SendHtml(cid, "<pre>" + Esc(PoliciesService.Privacy) + "</pre>").ConfigureAwait(false); break;
            case "/terms": await SendHtml(cid, "<pre>" + Esc(PoliciesService.Terms) + "</pre>").ConfigureAwait(false); break;
            case "/safety": case "/warning": case "/danger":
                await SendHtml(cid, "<pre>" + Esc(PoliciesService.Safety) + "</pre>").ConfigureAwait(false); break;
            case "/wake":
                await SendHtml(cid, Esc(RemoteInputService.WakeOnly())).ConfigureAwait(false);
                break;
            case "/unlock":
                if (!await RequireAdmin(cid).ConfigureAwait(false)) break;
                if (string.IsNullOrWhiteSpace(arg))
                {
                    SetPending(cid, async v => await UnlockWithPassword(cid, v).ConfigureAwait(false));
                    await Ask(cid, "<b>Enter Windows password</b> (it will be typed on the lock screen, message is deleted from chat history if you delete it):").ConfigureAwait(false);
                    break;
                }
                await UnlockWithPassword(cid, arg).ConfigureAwait(false);
                break;
            case "/wol":
                if (!await RequireAdmin(cid).ConfigureAwait(false)) break;
                if (string.IsNullOrWhiteSpace(arg))
                {
                    SetPending(cid, async v => await SendHtml(cid, Esc(WakeOnLanService.Send(v.Trim()))).ConfigureAwait(false));
                    await Ask(cid, "<b>Enter MAC</b> (AA:BB:CC:DD:EE:FF [broadcast]):\n<i>Works in LAN / via router with WoL-forward / VPN. PC off = bot off, so send from a second device or router.</i>").ConfigureAwait(false);
                    break;
                }
                await SendHtml(cid, Esc(WakeOnLanService.Send(arg.Split(' ')[0], arg.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 1 ? arg.Split(' ')[1] : "255.255.255.255"))).ConfigureAwait(false);
                break;
            case "/hibernate":
                if (!await RequireAdmin(cid).ConfigureAwait(false)) break;
                await Exec("shutdown /h").ConfigureAwait(false);
                await SendHtml(cid, "PC is hibernating.").ConfigureAwait(false);
                break;
            case "/ip": await SendHtml(cid, await GetIpCard().ConfigureAwait(false)).ConfigureAwait(false); break;
            case "/ping":
                await SendHtml(cid, "<i>Pinging 8.8.8.8…</i>").ConfigureAwait(false);
                await SendHtml(cid, $"Ping 8.8.8.8: <b>{await new NetworkService().TestLatency().ConfigureAwait(false)} ms</b>").ConfigureAwait(false);
                break;
            case "/uptime":
                await SendHtml(cid, "<b>Uptime:</b> " + Esc(SystemUptime.UptimeText)).ConfigureAwait(false);
                break;
            case "/battery": await SendHtml(cid, Esc(GetBatteryLine())).ConfigureAwait(false); break;
            case "/free": await SendHtml(cid, Esc(GetFreeSpace())).ConfigureAwait(false); break;
            case "/perf": await SendHtml(cid, "<pre>" + Esc(PerfText()) + "</pre>").ConfigureAwait(false); break;
            case "/sysinfo": await SendHtml(cid, "<pre>" + Esc(SysInfoText()) + "</pre>").ConfigureAwait(false); break;
            case "/startup":
                if (!await RequireAdmin(cid).ConfigureAwait(false)) break;
                await SendHtml(cid, "<pre>" + Esc(StartupText()) + "</pre>").ConfigureAwait(false);
                break;
            case "/netstat":
                await SendHtml(cid, "<i>Reading connections…</i>").ConfigureAwait(false);
                await SendMsg(cid, await Exec("netstat -ano | findstr ESTABLISHED").ConfigureAwait(false)).ConfigureAwait(false);
                break;
            case "/wifi":
                await SendHtml(cid, "<i>Scanning Wi-Fi…</i>").ConfigureAwait(false);
                await SendMsg(cid, await Exec("netsh wlan show networks mode=bssid").ConfigureAwait(false)).ConfigureAwait(false);
                break;
            case "/defender": await SendHtml(cid, "<pre>" + Esc(SecurityService.DefenderStatus()) + "</pre>").ConfigureAwait(false); break;
            case "/plans":
                await SendHtml(cid, "<pre>" + Esc(PowerPlansText()) + "</pre>").ConfigureAwait(false);
                break;
            case "/remote":
                await SendHtml(cid, "<b>Remote access</b>\n<pre>" +
                    Esc(RemoteAccessService.StatusText()) + "</pre>").ConfigureAwait(false);
                break;
            case "/fan": await SendHtml(cid, "<pre>" + Esc(FanControlService.StatusText()) + "</pre>").ConfigureAwait(false); break;
            case "/fan_mode":
                if (string.IsNullOrWhiteSpace(arg))
                    await SendHtml(cid, "Use: <code>/fan_mode Auto|Silent|Balanced|Performance|Manual[:percent]</code>").ConfigureAwait(false);
                else
                {
                    var parts = arg.Split(':', StringSplitOptions.RemoveEmptyEntries);
                    int pct = 50;
                    if (parts.Length > 1) int.TryParse(new string(parts[1].Where(char.IsDigit).ToArray()), out pct);
                    await SendHtml(cid, Esc(FanControlService.ApplyMode(parts[0].Trim(), pct))).ConfigureAwait(false);
                }
                break;
            case "/hotkey":
                if (string.IsNullOrWhiteSpace(arg))
                    await SendHtml(cid, "Use: <code>/hotkey optimize|gameboost|widget|screenshot|mute|lock</code>").ConfigureAwait(false);
                else
                    await SendHtml(cid, Esc(await HotkeyActionRunner.RunAsync(arg.Split(' ')[0]).ConfigureAwait(false))).ConfigureAwait(false);
                break;
            case "/eventlog":
                if (!await RequireAdmin(cid).ConfigureAwait(false)) break;
                await SendMsg(cid, await Exec("wevtutil qe System /c:15 /f:text /rd:true").ConfigureAwait(false)).ConfigureAwait(false);
                break;
            case "/users":
                int au, ad;
                lock (_stateLock) { au = _authorizedUsers.Count; ad = _adminUsers.Count; }
                await SendHtml(cid, $"Authorized: <b>{au}</b> (admins: <b>{ad}</b>)").ConfigureAwait(false);
                break;
            case "/log":
            {
                string[] lines;
                lock (_stateLock) lines = _actionLog.Take(10).ToArray();
                await SendHtml(cid, "<b>Recent actions:</b>\n" + Esc(string.Join("\n", lines))).ConfigureAwait(false);
                break;
            }

            default:
                await SendMainMenu(cid).ConfigureAwait(false);
                break;
        }
    }

    private void SetPending(long cid, Func<string, Task> fn)
    {
        lock (_stateLock) _pendingActions[cid] = fn;
    }

    // Текст кнопок ReplyKeyboard → команды (и со старыми эмодзи-вариантами для совместимости)
    private static string? MapButtonToCommand(string cmd) => cmd switch
    {
        "status" => "/status",
        "processes" => "/processes",
        "apps" => "/apps",
        "volume" => "/volume",
        "brightness" => "/brightness",
        "screenshot" => "/screenshot",
        "stream" => "/stream",
        "stop" => "/stop",
        "cam" => "/cam",
        "files" => "/files",
        "shutdown" => "/shutdown",
        "restart" => "/restart",
        "sleep" => "/sleep",
        "hibernate" => "/hibernate",
        "wake" => "/wake",
        "unlock" => "/unlock",
        "wol" => "/wol",
        "clean" => "/clean",
        "optimize ram" => "/optimize_ram",
        "cmd" => "/cmd",
        "open app" => "/open",
        "close app" => "/close",
        "lock" => "/lock",
        "license" => "/license",
        "live" => "/live",
        "privacy" => "/privacy",
        "terms" => "/terms",
        "safety" => "/safety",
        "help" => "/help",
        _ => null
    };

    private static ReplyKeyboardMarkup QuickKeyboard(params string[] buttons)
    {
        var rows = new List<KeyboardButton[]>();
        for (int i = 0; i < buttons.Length; i += 3)
            rows.Add(buttons.Skip(i).Take(3).Select(b => new KeyboardButton(b)).ToArray());
        rows.Add(new[] { new KeyboardButton("Cancel") });
        return new ReplyKeyboardMarkup(rows) { ResizeKeyboard = true, OneTimeKeyboard = true };
    }

    private static ReplyKeyboardMarkup CancelKeyboard() => new(new[]
    {
        new[] { new KeyboardButton("Cancel") }
    })
    { ResizeKeyboard = true, OneTimeKeyboard = true };

    private Task Ask(long cid, string html) =>
        SendHtml(cid, html + "\n\n<i>Send “Cancel” to abort.</i>", CancelKeyboard());

    private async Task<bool> RequireAdmin(long cid)
    {
        if (IsAdmin(cid)) return true;
        await SendHtml(cid, "Admins only. Ask the owner for access.").ConfigureAwait(false);
        return false;
    }

    private async Task SendStatusCard(long cid)
    {
        string card;
        try
        {
            var (totalGb, availGb) = DetailedSystemInfoService.GetPhysicalMemory();
            var usedGb = Math.Max(0, totalGb - availGb);
            var ramPct = totalGb > 0 ? usedGb / totalGb * 100 : 0;
            string drives;
            try
            {
                drives = string.Join("\n", DriveInfo.GetDrives()
                    .Where(d => d.IsReady && d.DriveType == DriveType.Fixed)
                    .Select(d => $"  • <b>{Esc(d.Name)}</b> {d.AvailableFreeSpace / 1073741824.0:F1} GB free"));
                if (string.IsNullOrWhiteSpace(drives)) drives = "  • —";
            }
            catch { drives = "  • —"; }

            var upStr = SystemUptime.UptimeText;

            card = $"<b>{Esc(Environment.MachineName)}</b> — <i>online</i>\n" +
                   $"<code>{Esc(Environment.OSVersion.VersionString)}</code> • {Environment.ProcessorCount} cores\n" +
                   $"Uptime: <b>{upStr}</b>\n\n" +
                   $"<b>RAM</b> {usedGb:F1}/{totalGb:F1} GB ({ramPct:F0}%)\n" +
                   $"{Bar(ramPct)}\n\n" +
                   $"<b>Disks</b>\n{drives}\n\n" +
                   $"{Esc(GetBatteryLine())}\n" +
                   $"<i>{DateTime.Now:G}</i>";
        }
        catch (Exception ex)
        {
            card = "Status failed: <code>" + Esc(Trim(ex.Message, 200)) + "</code>";
        }
        await SendHtml(cid, card).ConfigureAwait(false);
    }

    private async Task SendLiveCard(long cid)
    {
        var tunnel = LiveServices.Tunnel;
        var url = tunnel.PublicUrl;
        if (string.IsNullOrEmpty(url) || !tunnel.IsRunning)
        {
            await SendHtml(cid,
                "<b>Live WebApp access is off.</b>\n\n" +
                "On the PC open SystemGuard → Telegram tab → <b>Publish live link</b>, " +
                "then enter the shown address + token in the WebApp <b>Status</b> tab.\n\n" +
                "Without live the WebApp shows a connect card; chat commands (/status, /cmd, …) keep working here.").ConfigureAwait(false);
            return;
        }
        await SendHtml(cid,
            "<b>Live access is ON.</b>\n\n" +
            $"Transport: <code>{Esc(tunnel.Provider ?? "?")}</code>\n" +
            $"Server: <code>{Esc(url)}</code>\n" +
            $"Token: <code>{Esc(LiveServices.Token)}</code>\n\n" +
            "Enter both in the WebApp <b>Status</b> tab: full PC mirror right inside the app.\n\n" +
            "<i>Note: the link changes on every Publish / reconnect — if the app stops connecting, send /live again for a fresh one.</i>").ConfigureAwait(false);
    }

    // Видео — только внутри WebApp: в чат кидаем ссылку, а не фото-спам.
    private async Task SendStreamHint(long cid)
    {
        var tunnel = LiveServices.Tunnel;
        var url = tunnel.PublicUrl;
        if (string.IsNullOrEmpty(url) || !tunnel.IsRunning)
        {
            await SendHtml(cid,
                "<b>Video lives in the Web App</b> (Экран tab, up to 60 FPS, full screen).\n\n" +
                "Live is currently <b>off</b>: on the PC open SystemGuard → Telegram tab → <b>Publish live link</b>, " +
                "then enter the address + token in the WebApp Status tab.\n\n" +
                "Single frames still work here: /screenshot, /cam.").ConfigureAwait(false);
            return;
        }
        await SendHtml(cid,
            "<b>Video lives in the Web App</b> (Экран tab, up to 60 FPS, full screen).\n\n" +
            "Open the <b>Web App</b> button below — screen and camera streams play inside the app.\n\n" +
            "Single frames still work here: /screenshot, /cam.").ConfigureAwait(false);
    }

    private static string Bar(double pct, int width = 10)
    {
        var fill = (int)Math.Round(Math.Clamp(pct, 0, 100) / 100 * width);
        return new string('█', fill) + new string('░', Math.Max(0, width - fill));
    }

    private static async Task<string> GetIpCard()
    {
        try
        {
            var ext = await new NetworkService().GetPublicIpAsync().ConfigureAwait(false);
            var local = string.Join(", ", System.Net.Dns.GetHostAddresses(System.Net.Dns.GetHostName())
                .Where(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork).Select(a => a.ToString()));
            return $"<b>Network</b>\n• External: <code>{Esc(ext)}</code>\n• Local: <code>{Esc(local)}</code>";
        }
        catch (Exception ex) { return Esc(ex.Message); }
    }

    private static string GetBatteryLine()
    {
        try
        {
            var b = new PowerService().GetBatteryInfo();
            return b == null ? "No battery (desktop)" : $"Battery: {b.ChargePercent}% ({b.Status})";
        }
        catch (Exception ex) { return Trim(ex.Message, 120); }
    }

    private static string PerfText()
    {
        try
        {
            var (totalGb, availGb) = DetailedSystemInfoService.GetPhysicalMemory();
            var used = Math.Max(0, totalGb - availGb);
            return $"CPU: {Environment.ProcessorCount} cores\nRAM: {used:F1}/{totalGb:F1} GB\nUptime: {SystemUptime.UptimeText}\n{GetFreeSpace()}\n{GetBatteryLine()}";
        }
        catch (Exception ex) { return ex.Message; }
    }

    private static string SysInfoText()
    {
        try
        {
            return $"Machine: {Environment.MachineName}\nOS: {Environment.OSVersion}\nUser: {Environment.UserName}\nUptime: {SystemUptime.UptimeText}\n" +
                   DetailedSystemInfoService.FormatSummary();
        }
        catch (Exception ex) { return ex.Message; }
    }

    private static string StartupText()
    {
        try
        {
            var items = new StartupService().GetStartupItems();
            return items.Count == 0 ? "Startup list is empty"
                : string.Join("\n", items.Select(s => $"{(s.IsEnabled ? "[ON] " : "[OFF]")} {s.Name}"));
        }
        catch (Exception ex) { return ex.Message; }
    }

    private static string PowerPlansText()
    {
        try
        {
            return string.Join("\n", new PowerService().GetPowerPlans().Select(p => $"{(p.IsActive ? "* " : "  ")}{p.Name}\n  {p.Guid}"));
        }
        catch (Exception ex) { return ex.Message; }
    }

    private static string GetFreeSpace()
    {
        try
        {
            var lines = new List<string>();
            foreach (var d in DriveInfo.GetDrives())
            {
                if (!d.IsReady || d.DriveType != DriveType.Fixed) continue;
                lines.Add($"{d.Name} {d.AvailableFreeSpace / 1073741824.0:F1} GB free of {d.TotalSize / 1073741824.0:F0} GB");
            }
            return lines.Count > 0 ? string.Join("\n", lines) : "No fixed drives";
        }
        catch (Exception ex) { return ex.Message; }
    }

    private async Task SetVolume(long cid, string arg)
    {
        var a = arg.Trim().ToLowerInvariant();
        if (a is "mute")
        {
            await Task.Run(() => VolumeService.MuteToggle()).ConfigureAwait(false);
            await Task.Delay(150).ConfigureAwait(false);
            await SendHtml(cid, VolumeService.IsMuted()
                ? "Sound <b>muted</b>."
                : $"Sound on. Volume: <b>{VolumeService.GetPercent()}%</b>").ConfigureAwait(false);
            return;
        }
        if (a is "up" or "+10" or "+")
        {
            await Task.Run(() => VolumeService.StepUp(3)).ConfigureAwait(false);
            await SendHtml(cid, $"Volume up: <b>{VolumeService.GetPercent()}%</b>").ConfigureAwait(false);
            return;
        }
        if (a is "down" or "-10" or "-")
        {
            await Task.Run(() => VolumeService.StepDown(3)).ConfigureAwait(false);
            await SendHtml(cid, $"Volume down: <b>{VolumeService.GetPercent()}%</b>").ConfigureAwait(false);
            return;
        }
        var digits = new string(a.Where(char.IsDigit).ToArray());
        if (int.TryParse(digits, out int vol))
        {
            var set = await Task.Run(() => VolumeService.SetPercent(vol)).ConfigureAwait(false);
            await SendHtml(cid, set >= 0
                ? $"Volume: <b>{set}%</b>"
                : "Volume control unavailable on this device.").ConfigureAwait(false);
        }
        else await SendHtml(cid, "Use: <code>0-100</code>, <code>up</code>, <code>down</code>, <code>mute</code>").ConfigureAwait(false);
    }

    private async Task SetBrightness(long cid, string arg)
    {
        var digits = new string(arg.Where(char.IsDigit).ToArray());
        if (!int.TryParse(digits, out int b))
        {
            await SendHtml(cid, "Use: <code>0-100</code>").ConfigureAwait(false);
            return;
        }
        var (ok, msg) = await BrightnessService.SetAsync(b).ConfigureAwait(false);
        await SendHtml(cid, ok ? $"<b>{Esc(msg)}</b>" : Esc(msg)).ConfigureAwait(false);
    }

    private async Task OpenApp(long cid, string name)
    {
        await SendHtml(cid, "<i>Opening…</i>").ConfigureAwait(false);
        var res = await RemoteActions.OpenAppAsync(name).ConfigureAwait(false);
        await SendHtml(cid, Esc(res)).ConfigureAwait(false);
    }

    private async Task KillProcess(long cid, string name)
    {
        var res = await RemoteActions.KillProcessAsync(name).ConfigureAwait(false);
        await SendHtml(cid, Esc(res)).ConfigureAwait(false);
    }

    private async Task SendWelcome(long cid)
    {
        if (!Ready()) return;
        await _botClient!.SendTextMessageAsync(cid,
            $"<b>SystemGuard Remote</b>\n<code>{Esc(Environment.MachineName)}</code>\n\n" +
            "Управляй своим ПК из Telegram: статус, файлы, скриншоты, питание, команды.\n" +
            "Открой <b>Web App</b> — там полная панель (всё внутри приложения, без закрытия).\n" +
            "<code>/live</code> — живая ссылка на экран и камеру.\n\n" +
            "<b>Pro:</b> расширенный пульт, live-доступ и автоматизация — <code>/tariffs</code>.\n" +
            "Купить Pro: шоп-бот @SystemGuardPayBot. Поддержка: @mattrix_solution.\n" +
            "<i>/help — все команды • /safety — что нельзя делать</i>",
            parseMode: ParseMode.Html, replyMarkup: MainKeyboard()).ConfigureAwait(false);
    }

    private static ReplyKeyboardMarkup MainKeyboard() => new(new[]
    {
        new[] { new KeyboardButton("Status"), new KeyboardButton("Processes"), new KeyboardButton("Apps") },
        new[] { new KeyboardButton("Volume"), new KeyboardButton("Brightness"), new KeyboardButton("Screenshot") },
        new[] { new KeyboardButton("Stream"), new KeyboardButton("Cam"), new KeyboardButton("Files") },
        new[] { new KeyboardButton("Shutdown"), new KeyboardButton("Restart"), new KeyboardButton("Sleep") },
        new[] { new KeyboardButton("Clean"), new KeyboardButton("Optimize RAM"), new KeyboardButton("CMD") },
        new[] { new KeyboardButton("Open App"), new KeyboardButton("Close App"), new KeyboardButton("Lock") },
        new[] { new KeyboardButton("Live"), new KeyboardButton("License"), new KeyboardButton("Help") },
        new[] { new KeyboardButton("Web App") { WebApp = new WebAppInfo { Url = "https://marik1337leet.github.io/systemguard-miniapp" } } }
    })
    { ResizeKeyboard = true };

    private async Task SendMainMenu(long cid)
    {
        try
        {
            await _botClient!.SendTextMessageAsync(cid, "<i>Choose an action:</i>",
                parseMode: ParseMode.Html, replyMarkup: MainKeyboard()).ConfigureAwait(false);
        }
        catch { }
    }

    private async Task SendHelp(long cid) => await SendHtml(cid,
        "<b>SystemGuard — справка</b>\n\n" +
        "<b>Мониторинг</b>\n" +
        "<code>/status</code> — карточка системы • <code>/perf</code> • <code>/sysinfo</code>\n" +
        "<code>/ip</code> • <code>/ping</code> • <code>/uptime</code> • <code>/battery</code> • <code>/free</code>\n\n" +
        "<b>Экран и файлы</b>\n" +
        "<code>/screenshot</code> • <code>/cam</code> — одиночные кадры в чат\n" +
        "<code>/stream</code> — видео только в Web App (Экран, до 60 FPS, весь экран)\n" +
        "<code>/ls C:\\</code> • <code>/get C:\\file</code> • <code>/processes</code> • <code>/apps</code>\n\n" +
        "<b>Питание и звук</b>\n" +
        "<code>/shutdown</code> • <code>/restart</code> • <code>/cancel</code> • <code>/sleep</code> • <code>/hibernate</code> • <code>/lock</code>\n" +
        "<code>/wake</code> • <code>/unlock</code> • <code>/wol AA:BB:CC:DD:EE:FF</code>\n" +
        "<code>/volume 70</code> • <code>/brightness 70</code> • <code>/play /next /prev /mute</code>\n\n" +
        "<b>Обслуживание</b>\n" +
        "<code>/clean</code> • <code>/ram</code> • <code>/open chrome</code> • <code>/close notepad</code> • <code>/cmd ipconfig</code>\n" +
        "<code>/startup</code> • <code>/netstat</code> • <code>/wifi</code> • <code>/defender</code> • <code>/plans</code> • <code>/eventlog</code> • <code>/remote</code>\n" +
        "<code>/fan</code> • <code>/fan_mode Silent</code> — вентиляторы\n" +
        "<code>/hotkey optimize</code> — действия хоткеев\n\n" +
        "<b>Лицензия</b>\n" +
        "<code>/tariffs</code> — что входит во Free и Pro, цены\n" +
        "<code>/buy</code> — купить Pro (Stars и криптоплатежка)\n" +
        "<code>/license</code> — то же меню покупки\n\n" +
        "<code>/privacy</code> • <code>/terms</code> • <code>/safety</code> — политики и опасности\n\n" +
        "<i>Аргументы работают в той же строке: <code>/ls C:\\Games</code>. Опасные команды — только для админов.</i>").ConfigureAwait(false);

    private async Task ListDir(long cid, string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir))
                dir = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            dir = dir.Trim().Trim('"');
            var blockedDir = SelfProtection.BlockedForRemoteRead(dir);
            if (blockedDir != null)
            {
                await SendHtml(cid, Esc(blockedDir) + ".").ConfigureAwait(false);
                return;
            }
            if (SelfProtection.IsProtectedPath(dir) && dir.TrimEnd('\\').Equals(SelfProtection.DataDirectory, StringComparison.OrdinalIgnoreCase))
            {
                await SendHtml(cid, "SystemGuard data folder is hidden.").ConfigureAwait(false);
                return;
            }
            if (!Directory.Exists(dir))
            {
                await SendHtml(cid, $"Not a folder: <code>{Esc(dir)}</code>").ConfigureAwait(false);
                return;
            }
            var entries = Directory.GetFileSystemEntries(dir).Take(30)
                .Select(e =>
                {
                    try
                    {
                        if (Directory.Exists(e)) return Esc(Path.GetFileName(e)) + "/";
                        var fi = new FileInfo(e);
                        return Esc(Path.GetFileName(e)) + $" ({fi.Length / 1024} KB)";
                    }
                    catch { return Esc(Path.GetFileName(e)); }
                });
            await SendHtml(cid, $"<code>{Esc(dir)}</code>\n{string.Join("\n", entries)}").ConfigureAwait(false);
        }
        catch (Exception ex) { await SendHtml(cid, $"List error: <code>{Esc(Trim(ex.Message, 300))}</code>").ConfigureAwait(false); }
    }

    private async Task SendFileToChat(long cid, string path)
    {
        try
        {
            path = path.Trim().Trim('"');
            var blocked = SelfProtection.BlockedForRemoteRead(path);
            if (blocked != null)
            {
                await SendHtml(cid, Esc(blocked)).ConfigureAwait(false);
                return;
            }
            if (!File.Exists(path))
            {
                await SendHtml(cid, "File not found.").ConfigureAwait(false);
                return;
            }
            var info = new FileInfo(path);
            if (info.Length > 49L * 1024 * 1024)
            {
                // Жёсткий лимит Bot API. Большие файлы — прямой ссылкой туннеля
                // из WebApp (Файлы tab, до 500 МБ).
                await SendHtml(cid, "File over 50 MB (Bot API hard limit).\nOpen <b>Web App → Файлы</b> for the direct tunnel link (up to 500 MB).").ConfigureAwait(false);
                return;
            }
            await using var s = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            await ThrottledSend(cid, (c, ct) =>
                _botClient!.SendDocumentAsync(c, InputFile.FromStream(s, info.Name),
                    caption: $"{Esc(info.Name)} ({info.Length / 1024} KB)",
                    cancellationToken: ct)).ConfigureAwait(false);
        }
        catch (Exception ex) { await SendHtml(cid, $"Send error: <code>{Esc(Trim(ex.Message, 300))}</code>").ConfigureAwait(false); }
    }

    private readonly Dictionary<long, CancellationTokenSource> _streams = new();
    private readonly object _streamLock = new();

    private void StartScreenStream(long cid)
    {
        lock (_streamLock)
        {
            StopScreenStream(cid);
            var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            _streams[cid] = cts;
            _ = Task.Run(() => StreamLoopAsync(cid, cts.Token));
        }
    }

    private void StopScreenStream(long cid)
    {
        lock (_streamLock)
        {
            if (_streams.TryGetValue(cid, out var cts))
            {
                try { cts.Cancel(); } catch { }
                try { cts.Dispose(); } catch { }
                _streams.Remove(cid);
            }
        }
    }

    private async Task StreamLoopAsync(long cid, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var jpg = await ScreenCaptureService.CaptureScreenJpegAsync(960, 45, ct).ConfigureAwait(false);
                if (jpg == null)
                {
                    try { await Task.Delay(3000, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
                    continue;
                }
                try
                {
                    using var ms = new MemoryStream(jpg);
                    await ThrottledSend(cid, (c, tok) =>
                        _botClient!.SendPhotoAsync(c, InputFile.FromStream(ms, "live.jpg"),
                            caption: "Live — /stop to end",
                            cancellationToken: tok)).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
                catch { }
                try { await Task.Delay(2500, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
            }
        }
        catch (OperationCanceledException) { }
        finally { lock (_streamLock) _streams.Remove(cid); }
    }

    private async Task SendWebcamToChat(long cid, string tag = "")
    {
        if (string.IsNullOrEmpty(tag))
            await SendHtml(cid, "<i>Capturing camera…</i>").ConfigureAwait(false);
        var (jpg, err) = await WebcamService.CaptureJpegAsync().ConfigureAwait(false);
        if (jpg == null)
        {
            await SendHtml(cid, $"Camera unavailable: <code>{Esc(err)}</code>").ConfigureAwait(false);
            return;
        }
        try
        {
            using var ms = new MemoryStream(jpg);
            await ThrottledSend(cid, (c, ct) =>
                _botClient!.SendPhotoAsync(c, InputFile.FromStream(ms, "cam.jpg"),
                    caption: $"Webcam • {DateTime.Now:HH:mm:ss}" + (string.IsNullOrEmpty(tag) ? "" : " " + tag),
                    cancellationToken: ct)).ConfigureAwait(false);
        }
        catch { await SendHtml(cid, "Camera upload failed.").ConfigureAwait(false); }
    }

    private async Task SendRunningApps(long cid)
    {
        var procs = await Task.Run(() => Process.GetProcesses()
            .Where(p => { try { return !string.IsNullOrEmpty(p.MainWindowTitle); } catch { return false; } })
            .OrderByDescending(p => { try { return p.WorkingSet64; } catch { return 0; } })
            .Take(15)
            .Select(p =>
            {
                string mem;
                try { mem = $"{p.WorkingSet64 / 1048576}MB"; } catch { mem = "?"; }
                var t = p.ProcessName;
                try { p.Dispose(); } catch { }
                return (t, mem);
            }).ToList()).ConfigureAwait(false);
        await SendHtml(cid, "<b>Running apps</b>\n" +
            string.Join("\n", procs.Select(p => $"• <code>{Esc(p.t)}</code> — {Esc(p.mem)}"))).ConfigureAwait(false);
    }

    // Важно: читаем вывод в OEM-кодировке консоли (CP866 на RU-Windows),
    // иначе кириллица превращается в кракозябры.
    private static Task<string> Exec(string cmd) => CmdEncoding.RunAsync(cmd);

    private async Task ExecuteAndSend(long cid, string cmd)
    {
        await SendHtml(cid, $"<code>{Esc(Trim(cmd, 200))}</code>\n<i>Running…</i>").ConfigureAwait(false);
        var r = CmdEncoding.Clean(await Exec(cmd).ConfigureAwait(false), 3800);
        await SendHtml(cid, $"<code>{Esc(Trim(cmd, 200))}</code>\n<pre>{Esc(r)}</pre>").ConfigureAwait(false);
    }

    private async Task ThrottledSend(long cid, Func<long, CancellationToken, Task> send)
    {
        await _sendGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var wait = MinSendInterval - (DateTime.UtcNow - _lastSendUtc);
            if (wait > TimeSpan.Zero) await Task.Delay(wait).ConfigureAwait(false);
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                await send(cid, cts.Token).ConfigureAwait(false);
            }
            catch (ApiRequestException ex) when (ex.ErrorCode == 429)
            {
                var delay = TimeSpan.FromSeconds(Math.Clamp(ex.Parameters?.RetryAfter ?? 3, 1, 60));
                await Task.Delay(delay).ConfigureAwait(false);
                try
                {
                    using var cts2 = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                    await send(cid, cts2.Token).ConfigureAwait(false);
                }
                catch { }
            }
            catch { }
            finally { _lastSendUtc = DateTime.UtcNow; }
        }
        finally { _sendGate.Release(); }
    }

    private async Task SendHtml(long cid, string html, IReplyMarkup? markup = null)
    {
        foreach (var chunk in Chunk(html, 4000))
        {
            var c = chunk;
            await ThrottledSend(cid, (chat, ct) =>
                _botClient!.SendTextMessageAsync(chat, c, parseMode: ParseMode.Html,
                    linkPreviewOptions: new LinkPreviewOptions { IsDisabled = true },
                    replyMarkup: markup, cancellationToken: ct)).ConfigureAwait(false);
            markup = null;
        }
    }

    private Task SendMsg(long cid, string msg)
    {
        var t = CmdEncoding.Clean(msg, 3800);
        return SendHtml(cid, "<pre>" + Esc(t) + "</pre>");
    }

    private static IEnumerable<string> Chunk(string s, int size)
    {
        if (string.IsNullOrEmpty(s)) { yield return "(empty)"; yield break; }
        for (int i = 0; i < s.Length; i += size)
            yield return s.Substring(i, Math.Min(size, s.Length - i));
    }

    public async Task<string> SendMessage(string msg)
    {
        try
        {
            if (_botClient == null || !IsConfigured) return "ERR: bot not configured";
            if (!long.TryParse(_chatId, out var id)) return "ERR: bad chat id";
            await SendHtml(id, Esc(msg)).ConfigureAwait(false);
            return "OK";
        }
        catch (Exception ex) { return $"Error: {Trim(ex.Message.Split('\n')[0], 200)}"; }
    }

    // На ПК сохраняется ВСЁ: документы, фото (лучшее качество), видео,
    // кружки, аудио и голосовые — раньше фото молча игнорировались.
    private async Task HandleFileUpload(Message msg)
    {
        var c = msg.Chat.Id;
        try
        {
            var dp = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "TG Downloads");
            Directory.CreateDirectory(dp);

            string? fileId = null;
            string fileName = "";
            if (msg.Document != null)
            {
                fileId = msg.Document.FileId;
                fileName = msg.Document.FileName ?? "file";
            }
            else if (msg.Photo != null && msg.Photo.Length > 0)
            {
                var best = msg.Photo.OrderByDescending(p => p.Width * p.Height).First();
                fileId = best.FileId;
                fileName = $"photo_{DateTime.Now:yyyyMMdd_HHmmss}.jpg";
            }
            else if (msg.Video != null)
            {
                fileId = msg.Video.FileId;
                fileName = msg.Video.FileName ?? $"video_{DateTime.Now:yyyyMMdd_HHmmss}.mp4";
            }
            else if (msg.VideoNote != null)
            {
                fileId = msg.VideoNote.FileId;
                fileName = $"videonote_{DateTime.Now:yyyyMMdd_HHmmss}.mp4";
            }
            else if (msg.Audio != null)
            {
                fileId = msg.Audio.FileId;
                fileName = msg.Audio.FileName ?? $"audio_{DateTime.Now:yyyyMMdd_HHmmss}.mp3";
            }
            else if (msg.Voice != null)
            {
                fileId = msg.Voice.FileId;
                fileName = $"voice_{DateTime.Now:yyyyMMdd_HHmmss}.ogg";
            }

            if (fileId == null)
            {
                await SendHtml(c, "Nothing to save: send a file, photo, video or audio.").ConfigureAwait(false);
                return;
            }

            await SendHtml(c, "<i>Downloading…</i>").ConfigureAwait(false);
            var f = await _botClient!.GetFileAsync(fileId).ConfigureAwait(false);
            var safe = string.Concat(fileName.Split(Path.GetInvalidFileNameChars()));
            if (string.IsNullOrWhiteSpace(safe)) safe = "file";
            var p = Path.Combine(dp, safe);
            if (File.Exists(p))
                p = Path.Combine(dp, Path.GetFileNameWithoutExtension(safe) +
                    $"_{DateTime.Now:HHmmss}" + Path.GetExtension(safe));
            var bytes = await _http.GetByteArrayAsync($"https://api.telegram.org/file/bot{_token}/{f.FilePath}").ConfigureAwait(false);
            await File.WriteAllBytesAsync(p, bytes).ConfigureAwait(false);
            await SendHtml(c, $"Saved to PC ({bytes.Length / 1024} KB):\n<code>{Esc(p)}</code>").ConfigureAwait(false);
        }
        catch (Exception ex) { await SendHtml(c, $"Upload error: <code>{Esc(Trim(ex.Message, 200))}</code>").ConfigureAwait(false); }
    }

    private async Task ScheduleChecker(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var n = DateTime.Now.ToString("HH:mm");
                List<BotTask> due;
                lock (_stateLock) due = _scheduledTasks.Where(t => t.Time == n).ToList();
                foreach (var t in due)
                {
                    try { await HandleCommand(t.Command, t.ChatId).ConfigureAwait(false); } catch { }
                    lock (_stateLock) _scheduledTasks.Remove(t);
                }
                await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch { try { await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; } }
        }
    }

    private async Task NotifyChecker(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_notifyEnabled)
                {
                    foreach (var d in DriveInfo.GetDrives().Where(d => d.IsReady && d.TotalFreeSpace / 1073741824.0 < 5))
                        await SendMessage($"Warning: low disk space on {d.Name}").ConfigureAwait(false);
                }
                await Task.Delay(TimeSpan.FromMinutes(5), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch { try { await Task.Delay(TimeSpan.FromMinutes(5), ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; } }
        }
    }

    private async Task ShowLicenseMenu(long cid)
    {
        if (!Ready()) return;
        await _botClient!.SendTextMessageAsync(cid,
        "<b>SystemGuard Pro</b>\n\n" +
        "Monthly — <b>300 Stars</b>\n" +
        "Half-Year — <b>1000 Stars</b>\n" +
        "Yearly — <b>1800 Stars</b>\n" +
        "Lifetime — <b>3000 Stars</b>\n\n" +
        "Оплата: Stars здесь + криптоплатежка в шоп-боте.\n" +
        "<i>После оплаты бот пришлёт ключ — вставь его в приложении (Settings → License → «подарочный ключ»). " +
        "Купленные через шоп-бота ключи привязываются к ПК автоматически.</i>\n\n" +
        "<i><code>/tariffs</code> — что входит во Free и Pro.</i>",
        parseMode: ParseMode.Html,
        replyMarkup: new InlineKeyboardMarkup(new[]
        {
            new[] { InlineKeyboardButton.WithCallbackData("Monthly — 300 Stars", "buy_monthly") },
            new[] { InlineKeyboardButton.WithCallbackData("Half-Year — 1000 Stars", "buy_halfyear") },
            new[] { InlineKeyboardButton.WithCallbackData("Yearly — 1800 Stars", "buy_yearly") },
            new[] { InlineKeyboardButton.WithCallbackData("Lifetime — 3000 Stars", "buy_lifetime") },
        })).ConfigureAwait(false);
    }

    /// <summary>Красивая справка по тарифам: что за что отвечает.</summary>
    private async Task SendTariffs(long cid)
    {
        if (!Ready()) return;
        await _botClient!.SendTextMessageAsync(cid,
        "<b>SystemGuard — тарифы</b>\n\n" +
        "<b>FREE (навсегда)</b>\n" +
        "• Мониторинг CPU/GPU/RAM/сети/дисков, история, Free Memory, Flush DNS\n" +
        "• Процессы: просмотр, Kill, Open Folder\n" +
        "• Автозагрузка: просмотр, Disable\n" +
        "• Очистка: Scan, базовая Clean, корзина\n" +
        "• Питание: Shutdown/Restart/Sleep/Lock, смена плана\n" +
        "• Defender: Quick Scan, Status\n\n" +
        "<b>PRO</b>\n" +
        "• Процессы: Force Kill, дерево, Suspend/Resume, дампы, VirusTotal, приоритеты\n" +
        "• Службы: Start/Stop • Глубокое удаление программ (3 шага)\n" +
        "• Игровой режим + твики системы • Бенчмарки и стресс-тесты\n" +
        "• Сеть: DNS, hosts, сканер портов/Wi-Fi, Wake-on-LAN\n" +
        "• Шифрование файлов, менеджер паролей\n" +
        "• Telegram-пульт: скриншоты, файлы, команды, стрим, камера, live-ссылка\n" +
        "• Планировщик задач\n\n" +
        "<b>Цены:</b> Monthly 300 • Half-Year 1000 • Yearly 1800 • Lifetime 3000 Stars.\n" +
        "Купить: <code>/buy</code>. Триал 14 дней — в приложении (Settings → License).",
        parseMode: ParseMode.Html).ConfigureAwait(false);
    }

    private async Task HandleCallback(string? d, long cid)
    {
        switch (d)
        {
            case "buy_monthly": await SendInvoice(cid, "Monthly", 300, 1).ConfigureAwait(false); break;
            case "buy_halfyear": await SendInvoice(cid, "Half-Year", 1000, 6).ConfigureAwait(false); break;
            case "buy_yearly": await SendInvoice(cid, "Yearly", 1800, 12).ConfigureAwait(false); break;
            case "buy_lifetime": await SendInvoice(cid, "Lifetime", 3000, 999).ConfigureAwait(false); break;
            default: await SendMainMenu(cid).ConfigureAwait(false); break;
        }
    }

    // Stars: currency XTR, providerToken не передаём вообще (иначе PAYMENT_PROVIDER_INVALID)
    private async Task SendInvoice(long cid, string n, int s, int m)
    {
        if (!Ready()) return;
        try
        {
            await _botClient!.SendInvoiceAsync(
                chatId: cid,
                title: $"SG Pro — {n}",
                description: $"Pro license: {n.ToLower()}",
                payload: $"{n}|{m}",
                currency: "XTR",
                prices: new[] { new LabeledPrice($"Pro {n}", s) }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await SendHtml(cid, $"Invoice failed: <code>{Esc(Trim(ex.Message, 200))}</code>").ConfigureAwait(false);
        }
    }

    private async Task OnSuccessfulPayment(Message msg)
    {
        try
        {
            var p = msg.SuccessfulPayment!;
            var parts = p.InvoicePayload.Split('|');
            var planName = parts[0];
            var m = int.Parse(parts[1]);
            var exp = m >= 999 ? DateTime.Now.AddYears(99) : DateTime.Now.AddMonths(m);
            var plan = m >= 999 ? "Lifetime" : planName;
            var key = GenerateKey("Pro", exp, plan);
            await _botClient!.SendTextMessageAsync(msg.Chat.Id,
                $"<b>Paid! Thank you!</b>\n\nActivate in the app (License tab):\nKey: <code>{key}</code>\nValid until {exp:dd MMM yyyy}",
                parseMode: ParseMode.Html).ConfigureAwait(false);
            // Stars падают на баланс бота → вывод владельцу через Fragment.
            // Дублируем чек владельцу, чтобы ни одна оплата не потерялась.
            try
            {
                var uname = msg.From?.Username != null ? "@" + msg.From.Username : $"id {msg.Chat.Id}";
                await _botClient.SendTextMessageAsync(PoliciesService.StarsOwnerId,
                    $"<b>Stars payment</b>\nPlan: <code>{Esc(planName)}</code> ({p.TotalAmount} XTR)\nFrom: {Esc(uname)} (chat <code>{msg.Chat.Id}</code>)\nKey: <code>{key}</code>\n<i>Withdraw via BotFather → My Bots → Payments → Fragment.</i>",
                    parseMode: ParseMode.Html).ConfigureAwait(false);
            }
            catch { /* владелец не в чате с ботом — чек уже у плательщика */ }
        }
        catch { }
    }

    private async Task UnlockWithPassword(long cid, string password)
    {
        password = (password ?? "").Trim();
        if (password.Length == 0)
        {
            await SendHtml(cid, "Empty password.").ConfigureAwait(false);
            return;
        }
        await SendHtml(cid, "<i>Waking display and typing password…</i>").ConfigureAwait(false);
        var res = await RemoteInputService.UnlockAsync(password).ConfigureAwait(false);
        // Сам пароль нигде не сохраняем и не показываем
        await SendHtml(cid, Esc(res) + "\n<i>Check screen stream. Delete your password message from chat.</i>").ConfigureAwait(false);
    }

    private static string GenerateKey(string tier, DateTime expiry, string plan)
    {
        var hmacSecret = System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes("SystemGuard_License_HMAC_Secret_2025"));
        var data = $"{tier}|{expiry:yyyy-MM-dd}|{plan}|";
        var dataBytes = Encoding.UTF8.GetBytes(data);
        using var hmac = new System.Security.Cryptography.HMACSHA256(hmacSecret);
        var sig = hmac.ComputeHash(dataBytes);
        static string B64(byte[] b) => Convert.ToBase64String(b).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        return $"SG-PRO-{B64(dataBytes)}.{B64(sig)}";
    }

    private async Task SendScreenshotToChat(long cid, string tag = "")
    {
        if (string.IsNullOrEmpty(tag))
            await SendHtml(cid, "<i>Capturing screen…</i>").ConfigureAwait(false);
        var jpg = await ScreenCaptureService.CaptureScreenJpegAsync().ConfigureAwait(false);
        if (jpg == null)
        {
            await SendHtml(cid, "Screenshot failed (no desktop / driver error).").ConfigureAwait(false);
            return;
        }
        try
        {
            using var ms = new MemoryStream(jpg);
            await ThrottledSend(cid, (c, ct) =>
                _botClient!.SendPhotoAsync(c, InputFile.FromStream(ms, "ss.jpg"),
                    caption: $"{Esc(Environment.MachineName)} • {DateTime.Now:HH:mm:ss}" + (string.IsNullOrEmpty(tag) ? "" : " " + tag),
                    cancellationToken: ct)).ConfigureAwait(false);
        }
        catch { await SendHtml(cid, "Screenshot upload failed.").ConfigureAwait(false); }
    }

    public async Task<string> SendScreenshot()
    {
        try
        {
            if (_botClient == null || !IsConfigured) return "ERR: bot not configured";
            if (!long.TryParse(_chatId, out var id)) return "ERR: bad chat id";
            var jpg = await ScreenCaptureService.CaptureScreenJpegAsync().ConfigureAwait(false);
            if (jpg == null) return "ERR: capture failed";
            try
            {
                using var ms = new MemoryStream(jpg);
                await ThrottledSend(id, (c, ct) =>
                    _botClient.SendPhotoAsync(c, InputFile.FromStream(ms, "ss.jpg"),
                        cancellationToken: ct)).ConfigureAwait(false);
                return "OK";
            }
            catch (Exception ex) { return $"ERR: {Trim(ex.Message.Split('\n')[0], 200)}"; }
        }
        catch (Exception ex) { return $"ERR: {Trim(ex.Message.Split('\n')[0], 200)}"; }
    }

    public bool IsStreaming { get { lock (_streamLock) return _streams.Count > 0; } }

    // Кнопка на ПК больше не спамит фото в чат: шлём подсказку про WebApp.
    public string StartStreamToConfigured()
    {
        if (!IsConfigured || !long.TryParse(_chatId, out var id)) return "Connect bot first";
        _ = SendStreamHint(id);
        return "Stream hint sent — video plays in the Web App";
    }

    public string StopAllStreams()
    {
        foreach (var id in _streams.Keys.ToList()) StopScreenStream(id);
        return "All streams stopped";
    }

    public async Task SendWebcamToConfigured()
    {
        if (!IsConfigured || !long.TryParse(_chatId, out var id)) return;
        await SendWebcamToChat(id).ConfigureAwait(false);
    }

    public Task SendFileToConfigured(string path) =>
        IsConfigured && long.TryParse(_chatId, out var id) && !string.IsNullOrWhiteSpace(path)
            ? SendFileToChat(id, path) : Task.CompletedTask;

    public async Task<string> ExecuteCommand(string cmd) => await Exec(cmd).ConfigureAwait(false);

    public Task<string> GetSystemStatus() => Task.FromResult(JsonSerializer.Serialize(new
    {
        Time = DateTime.Now.ToString("G"),
        Machine = Environment.MachineName,
        OS = Environment.OSVersion.ToString(),
        CPU = Environment.ProcessorCount,
        RAM = $"{DetailedSystemInfoService.GetTotalPhysicalGb():F0}GB",
        Drives = string.Join(", ", DriveInfo.GetDrives().Where(d => d.IsReady).Select(d => $"{d.Name} {d.TotalFreeSpace / 1073741824}GB free"))
    }, new JsonSerializerOptions { WriteIndented = true }));

    private static string Escape(string t) => t.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
    private static string Esc(string? t) => Escape(t ?? "");
    private static string Trim(string? s, int n) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s[..n] + "…");

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { StopListening(); } catch { }
        try { _sendGate.Dispose(); } catch { }
    }
}

internal class BotTask
{
    public string Command { get; set; } = "";
    public string Time { get; set; } = "";
    public long ChatId { get; set; }
}

