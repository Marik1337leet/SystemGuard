using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
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

// Полный рерайт v2: бот больше не вешает ни себя, ни приложение.
//   • polling-цикл никогда не блокируется командами (каждый апдейт — отдельная задача);
//   • отправка через шлюз с rate-limit + уважением к FloodWait (429 RetryAfter);
//   • длинные выводы режутся на чанки, весь пользовательский текст HTML-экранируется;
//   • команды понимают аргумент в том же сообщении: "/ls C:\" "/cmd ipconfig" "/volume 80";
//   • двухшаговый ввод остался как fallback, когда аргумента нет;
//   • MiniApp (WebApp) шлёт команды через Telegram.WebApp.sendData → web_app_data;
//   • красивые HTML-карточки вместо сырого текста.
public sealed class TelegramBotService : IDisposable
{
    private string _token = "", _chatId = "";
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(60) };
    private TelegramBotClient? _botClient;
    private CancellationTokenSource? _cts;
    private Task? _listenTask, _scheduleTask, _notifyTask;
    private int _listening; // 0/1
    private bool _disposed;
    private bool _notifyEnabled;

    private readonly List<long> _authorizedUsers = new();
    private readonly List<long> _adminUsers = new();
    private readonly List<BotTask> _scheduledTasks = new();
    private readonly List<string> _actionLog = new();
    private readonly Dictionary<long, Func<string, Task>> _pendingActions = new();
    private readonly object _stateLock = new();

    // ── Шлюз отправки: один мьютекс + минимальный интервал, чтобы не ловить 429 ──
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
        if (Interlocked.Exchange(ref _listening, 1) == 1) return; // уже слушаем
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
        // Не ждём задачи синхронно (могут звать из UI) — просто отпускаем ссылки
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

    // ── Polling: лёгкий цикл, тяжёлое — в отдельных задачах ─────────────────────
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
                    // Не блокируем цикл: каждый апдейт живёт своей жизнью
                    _ = Task.Run(() => HandleUpdateSafe(u), ct);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (ApiRequestException ex) when (ex.ErrorCode == 409)
            {
                // Второй инстанс бота с тем же токеном — ждём и пробуем дальше, а не спамим
                try { await Task.Delay(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
            catch (Exception)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task HandleUpdateSafe(Update u)
    {
        try { await HandleUpdate(u).ConfigureAwait(false); }
        catch { /* один апдейт никогда не роняет цикл */ }
    }

    private async Task HandleUpdate(Update u)
    {
        if (u.CallbackQuery != null)
        {
            var cq = u.CallbackQuery;
            var cid = cq.Message?.Chat.Id ?? 0;
            if (cid != 0 && !IsAuthorized(cid))
            {
                try { await _botClient!.AnswerCallbackQueryAsync(cq.Id, "⛔ Unauthorized").ConfigureAwait(false); }
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

        // MiniApp: Telegram.WebApp.sendData(...) прилетает сюда как web_app_data
        if (msg.WebAppData != null)
        {
            if (!IsAuthorized(cid2))
            {
                await SendHtml(cid2, "⛔ <b>Unauthorized.</b> Ask the PC owner to add your chat ID.").ConfigureAwait(false);
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
                await SendHtml(cid2, "⛔ <b>Unauthorized.</b>\nYour chat ID: <code>" + cid2 + "</code>").ConfigureAwait(false);
                return;
            }
            Log(Trim(msg.Text, 120), cid2);
            await HandleCommand(msg.Text, cid2).ConfigureAwait(false);
        }
    }

    // ── WebApp JSON-команды: {"action":"status"} {"action":"volume","arg":"70"} ──
    private async Task HandleWebAppData(string data, long cid)
    {
        string action = "", arg = "";
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
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["power"] = "power", ["dashboard"] = "status", ["info"] = "status",
            ["mute"] = "mute", ["play"] = "play", ["pause"] = "pause",
        };
        if (map.TryGetValue(action, out var m)) action = m;

        await HandleCommand("/" + action + (string.IsNullOrWhiteSpace(arg) ? "" : " " + arg.Trim()), cid)
            .ConfigureAwait(false);
    }

    // ── Парсинг: "/ls C:\" или "Status", кнопки клавиатуры, webapp-экшены ───────
    private static string ExtractCommand(string text)
    {
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
            if (part.StartsWith("/"))
            {
                var c = part.ToLower().Trim();
                var at = c.IndexOf('@'); // "/status@MyBot" → "/status"
                return at > 0 ? c[..at] : c;
            }
        return text.ToLower().Trim();
    }

    private static string ExtractArg(string text)
    {
        var i = text.IndexOf(' ');
        return i < 0 ? "" : text[(i + 1)..].Trim().Trim('"', ' ');
    }

    private async Task HandleCommand(string rawText, long cid)
    {
        var text = (rawText ?? "").Trim();
        if (text.Length == 0) return;

        // Ответ на двухшаговый запрос
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
            catch (Exception ex) { await SendHtml(cid, "⚠️ " + Esc(Trim(ex.Message, 300))).ConfigureAwait(false); }
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
            case "/status": await SendStatusCard(cid).ConfigureAwait(false); break;
            case "/apps": await SendRunningApps(cid).ConfigureAwait(false); break;
            case "/processes":
                await SendHtml(cid, "⏳ <i>Collecting process list…</i>").ConfigureAwait(false);
                await SendMsg(cid, await Exec("tasklist /fo table").ConfigureAwait(false)).ConfigureAwait(false);
                break;
            case "/shutdown":
                if (!await RequireAdmin(cid).ConfigureAwait(false)) break;
                await Exec("shutdown /s /t 60").ConfigureAwait(false);
                await SendHtml(cid, "🔌 <b>Shutdown in 60 seconds.</b>\n/cancel — abort").ConfigureAwait(false);
                break;
            case "/restart":
                if (!await RequireAdmin(cid).ConfigureAwait(false)) break;
                await Exec("shutdown /r /t 60").ConfigureAwait(false);
                await SendHtml(cid, "🔄 <b>Restart in 60 seconds.</b>\n/cancel — abort").ConfigureAwait(false);
                break;
            case "/cancel":
                if (!await RequireAdmin(cid).ConfigureAwait(false)) break;
                await Exec("shutdown /a").ConfigureAwait(false);
                await SendHtml(cid, "✅ Shutdown timer cancelled.").ConfigureAwait(false);
                break;
            case "/sleep":
                await Exec("rundll32.exe powrprof.dll,SetSuspendState 0,1,0").ConfigureAwait(false);
                await SendHtml(cid, "🌙 PC is going to <b>sleep</b>.").ConfigureAwait(false);
                break;
            case "/lock":
                await Exec("rundll32.exe user32.dll,LockWorkStation").ConfigureAwait(false);
                await SendHtml(cid, "🔒 Workstation <b>locked</b>.").ConfigureAwait(false);
                break;
            case "/screenshot": await SendScreenshotToChat(cid).ConfigureAwait(false); break;
            case "/stream":
                StartScreenStream(cid);
                await SendHtml(cid, "📡 <b>Live screen started</b> (~1 frame / 2.5s, auto-stop in 5 min).\n/stop — end").ConfigureAwait(false);
                break;
            case "/stop":
                StopScreenStream(cid);
                await SendHtml(cid, "🛑 Stream stopped.").ConfigureAwait(false);
                break;
            case "/cam": await SendWebcamToChat(cid).ConfigureAwait(false); break;
            case "/mute":
                await Exec("powershell -Command \"$ws=New-Object -ComObject WScript.Shell;$ws.SendKeys([char]173)\"").ConfigureAwait(false);
                await SendHtml(cid, "🔇 Muted.").ConfigureAwait(false);
                break;

            case "/ls":
                if (!string.IsNullOrWhiteSpace(arg)) { await ListDir(cid, arg).ConfigureAwait(false); break; }
                SetPending(cid, async v => await ListDir(cid, v).ConfigureAwait(false));
                await Ask(cid, "📁 <b>Enter folder path</b> (empty = Desktop):").ConfigureAwait(false);
                break;
            case "/get":
                if (!string.IsNullOrWhiteSpace(arg)) { await SendFileToChat(cid, arg).ConfigureAwait(false); break; }
                SetPending(cid, async v => await SendFileToChat(cid, v).ConfigureAwait(false));
                await Ask(cid, "📥 <b>Enter file path on PC:</b>").ConfigureAwait(false);
                break;
            case "/clean":
                if (!await RequireAdmin(cid).ConfigureAwait(false)) break;
                await SendHtml(cid, "🧹 <i>Cleaning temp files…</i>").ConfigureAwait(false);
                var r = await Exec("del /q /f %temp%\\* 2>nul & echo Done").ConfigureAwait(false);
                await SendHtml(cid, "🧹 <b>Cleanup:</b> <code>" + Esc(Trim(r, 500)) + "</code>").ConfigureAwait(false);
                break;
            case "/optimize_ram": case "/ram":
                await Exec("powershell [GC]::Collect()").ConfigureAwait(false);
                await SendHtml(cid, "⚡ RAM trim requested.").ConfigureAwait(false);
                break;
            case "/play": case "/pause":
                await MediaKey(0xB3).ConfigureAwait(false);
                await SendHtml(cid, "⏯ Play/Pause.").ConfigureAwait(false);
                break;
            case "/next": await MediaKey(0xB0).ConfigureAwait(false); await SendHtml(cid, "⏭ Next track.").ConfigureAwait(false); break;
            case "/prev": await MediaKey(0xB1).ConfigureAwait(false); await SendHtml(cid, "⏮ Previous track.").ConfigureAwait(false); break;
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
            case "/files":
                await SendHtml(cid,
                    "📂 <b>Files</b>\n• <code>/ls C:\\</code> — list folder\n• <code>/get C:\\a.txt</code> — download from PC\n• Just send any file/photo — it lands in <b>Desktop\\TG Downloads</b>").ConfigureAwait(false);
                break;

            case "/volume":
                if (!string.IsNullOrWhiteSpace(arg)) { await SetVolume(cid, arg).ConfigureAwait(false); break; }
                SetPending(cid, async v => await SetVolume(cid, v).ConfigureAwait(false));
                await _botClient!.SendTextMessageAsync(cid, "🔊 Enter volume (0-100), 'up', 'down' or 'mute':",
                    replyMarkup: QuickKeyboard("80", "50", "20", "up", "down", "mute")).ConfigureAwait(false);
                break;

            case "/brightness":
                if (!string.IsNullOrWhiteSpace(arg)) { await SetBrightness(cid, arg).ConfigureAwait(false); break; }
                SetPending(cid, async v => await SetBrightness(cid, v).ConfigureAwait(false));
                await _botClient!.SendTextMessageAsync(cid, "🔆 Enter brightness (0-100):",
                    replyMarkup: QuickKeyboard("100", "70", "40")).ConfigureAwait(false);
                break;

            case "/open":
                if (!string.IsNullOrWhiteSpace(arg)) { await OpenApp(cid, arg).ConfigureAwait(false); break; }
                SetPending(cid, async v => await OpenApp(cid, v).ConfigureAwait(false));
                await _botClient!.SendTextMessageAsync(cid, "🚀 Enter app name:",
                    replyMarkup: QuickKeyboard("chrome", "steam", "notepad")).ConfigureAwait(false);
                break;

            case "/close":
                if (!await RequireAdmin(cid).ConfigureAwait(false)) break;
                if (!string.IsNullOrWhiteSpace(arg)) { await KillProcess(cid, arg).ConfigureAwait(false); break; }
                SetPending(cid, async v => await KillProcess(cid, v).ConfigureAwait(false));
                await Ask(cid, "❌ <b>Enter process name to terminate:</b>").ConfigureAwait(false);
                break;

            case "/cmd":
                if (!await RequireAdmin(cid).ConfigureAwait(false)) break;
                if (!string.IsNullOrWhiteSpace(arg)) { await ExecuteAndSend(cid, arg).ConfigureAwait(false); break; }
                SetPending(cid, async v => await ExecuteAndSend(cid, v).ConfigureAwait(false));
                await Ask(cid, "💻 <b>Enter CMD command:</b>").ConfigureAwait(false);
                break;

            case "/ip": await SendHtml(cid, await GetIpCard().ConfigureAwait(false)).ConfigureAwait(false); break;
            case "/ping":
                await SendHtml(cid, "📶 <i>Pinging 8.8.8.8…</i>").ConfigureAwait(false);
                await SendHtml(cid, $"📶 Ping 8.8.8.8: <b>{await new NetworkService().TestLatency().ConfigureAwait(false)} ms</b>").ConfigureAwait(false);
                break;
            case "/uptime":
                await SendHtml(cid, $"⏱ <b>Uptime:</b> {Esc($"{TimeSpan.FromMilliseconds(Environment.TickCount64):d\\d\\ h\\h\\ m\\m}")}").ConfigureAwait(false);
                break;
            case "/battery": await SendHtml(cid, "🔋 " + Esc(GetBatteryLine())).ConfigureAwait(false); break;
            case "/free": await SendHtml(cid, "💾\n" + Esc(GetFreeSpace())).ConfigureAwait(false); break;
            case "/users":
                int au, ad;
                lock (_stateLock) { au = _authorizedUsers.Count; ad = _adminUsers.Count; }
                await SendHtml(cid, $"👥 Authorized: <b>{au}</b> (admins: <b>{ad}</b>)").ConfigureAwait(false);
                break;
            case "/log":
            {
                string[] lines;
                lock (_stateLock) lines = _actionLog.Take(10).ToArray();
                await SendHtml(cid, "🧾 <b>Recent actions:</b>\n" + Esc(string.Join("\n", lines))).ConfigureAwait(false);
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

    private static string? MapButtonToCommand(string cmd) => cmd switch
    {
        "status" => "/status",
        "🖥 status" => "/status",
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
        "clean" => "/clean",
        "optimize ram" => "/optimize_ram",
        "cmd" => "/cmd",
        "open app" => "/open",
        "close app" => "/close",
        "lock" => "/lock",
        "license" => "/license",
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
        await SendHtml(cid, "⛔ Admins only. Ask the owner for access.").ConfigureAwait(false);
        return false;
    }

    // ── Красивые карточки ───────────────────────────────────────────────────────
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
                    .Select(d => $"   • <b>{Esc(d.Name)}</b> {d.AvailableFreeSpace / 1073741824.0:F1} GB free"));
                if (string.IsNullOrWhiteSpace(drives)) drives = "   • —";
            }
            catch { drives = "   • —"; }

            string batt = GetBatteryLine();
            var up = TimeSpan.FromMilliseconds(Environment.TickCount64);
            var upStr = up.TotalDays >= 1 ? $"{(int)up.TotalDays}d {up.Hours}h {up.Minutes}m"
                : up.TotalHours >= 1 ? $"{(int)up.TotalHours}h {up.Minutes}m" : $"{up.Minutes}m";

            card = $"🖥 <b>{Esc(Environment.MachineName)}</b> — <i>online</i>\n" +
                   $"🧩 <code>{Esc(Environment.OSVersion.VersionString)}</code> • {Environment.ProcessorCount} cores\n" +
                   $"⏱ Uptime: <b>{upStr}</b>\n\n" +
                   $"🧠 <b>RAM</b> {usedGb:F1}/{totalGb:F1} GB ({ramPct:F0}%)\n" +
                   $"{Bar(ramPct)}\n\n" +
                   $"💾 <b>Disks</b>\n{drives}\n\n" +
                   $"🔋 {Esc(batt)}\n" +
                   $"🕒 <i>{DateTime.Now:G}</i>";
        }
        catch (Exception ex)
        {
            card = "⚠️ Status failed: <code>" + Esc(Trim(ex.Message, 200)) + "</code>";
        }
        await SendHtml(cid, card).ConfigureAwait(false);
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
            return $"🌐 <b>Network</b>\n• External: <code>{Esc(ext)}</code>\n• Local: <code>{Esc(local)}</code>";
        }
        catch (Exception ex) { return "⚠️ " + Esc(ex.Message); }
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

    // ── MEDIA KEYS ──
    [DllImport("user32.dll")] private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
    private static async Task MediaKey(byte key)
    {
        await Task.Run(() =>
        {
            keybd_event(key, 0, 0, UIntPtr.Zero);
            Thread.Sleep(60);
            keybd_event(key, 0, 2, UIntPtr.Zero);
        }).ConfigureAwait(false);
    }

    private async Task SetVolume(long cid, string arg)
    {
        arg = arg.Trim().ToLowerInvariant();
        if (arg is "mute" or "0")
        {
            await Exec("powershell -Command \"$ws=New-Object -ComObject WScript.Shell;$ws.SendKeys([char]173)\"").ConfigureAwait(false);
            await SendHtml(cid, "🔇 Muted.").ConfigureAwait(false);
            return;
        }
        if (arg is "up" or "+10" or "down" or "-10")
        {
            bool up = arg.Contains("up") || arg.StartsWith("+");
            await Exec($"powershell -Command \"$ws=New-Object -ComObject WScript.Shell;for($i=0;$i -lt 5;$i++){{$ws.SendKeys([char]{(up ? 175 : 174)})}}\"").ConfigureAwait(false);
            await SendHtml(cid, up ? "🔊 Volume up." : "🔉 Volume down.").ConfigureAwait(false);
            return;
        }
        var digits = new string(arg.Where(char.IsDigit).ToArray());
        if (int.TryParse(digits, out int vol))
        {
            vol = Math.Clamp(vol, 0, 100);
            // Точное значение — через CoreAudio (nircmd не требуем): ступенями к цели
            await Exec($"powershell -Command \"$ws=New-Object -ComObject WScript.Shell;for($i=0;$i -lt 50;$i++){{$ws.SendKeys([char]174)}};for($i=0;$i -lt {(vol / 2)};$i++){{$ws.SendKeys([char]175)}}\"").ConfigureAwait(false);
            await SendHtml(cid, $"🔊 Volume ≈ <b>{vol}%</b>").ConfigureAwait(false);
        }
        else await SendHtml(cid, "Use: <code>0-100</code>, <code>up</code>, <code>down</code>, <code>mute</code>").ConfigureAwait(false);
    }

    private async Task SetBrightness(long cid, string arg)
    {
        var digits = new string(arg.Where(char.IsDigit).ToArray());
        if (int.TryParse(digits, out int b) && b >= 0 && b <= 100)
        {
            await Exec($"powershell (Get-WmiObject -Namespace root/WMI -Class WmiMonitorBrightnessMethods).WmiSetBrightness(1,{b})").ConfigureAwait(false);
            await SendHtml(cid, $"🔆 Brightness: <b>{b}%</b>").ConfigureAwait(false);
        }
        else await SendHtml(cid, "Use: <code>0-100</code>").ConfigureAwait(false);
    }

    private async Task OpenApp(long cid, string name)
    {
        name = name.Trim().Trim('"');
        if (name.Length == 0) { await SendHtml(cid, "Enter app name.").ConfigureAwait(false); return; }
        try
        {
            var n = name.Replace(".exe", "").Trim();
            if (Process.GetProcessesByName(n).Length > 0)
            {
                await SendHtml(cid, $"ℹ️ <code>{Esc(n)}</code> is already running.").ConfigureAwait(false);
                return;
            }
            Process.Start(new ProcessStartInfo(name) { UseShellExecute = true });
            await SendHtml(cid, $"🚀 Opening <code>{Esc(name)}</code>…").ConfigureAwait(false);
        }
        catch
        {
            foreach (var path in new[]
            {
                @"C:\Program Files", @"C:\Program Files (x86)",
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)
            })
            {
                try
                {
                    if (!Directory.Exists(path)) continue;
                    var files = Directory.EnumerateFiles(path, "*.exe", SearchOption.AllDirectories)
                        .Where(f => Path.GetFileName(f).Contains(name.Replace(".exe", ""),
                            StringComparison.OrdinalIgnoreCase)).Take(2).ToList();
                    foreach (var f in files)
                    {
                        Process.Start(new ProcessStartInfo(f) { UseShellExecute = true });
                        await SendHtml(cid, $"🚀 Opened: <code>{Esc(Path.GetFileName(f))}</code>").ConfigureAwait(false);
                        return;
                    }
                }
                catch { }
            }
            await SendHtml(cid, $"❌ Not found: <code>{Esc(name)}</code>").ConfigureAwait(false);
        }
    }

    private async Task KillProcess(long cid, string name)
    {
        try
        {
            var n = name.Replace(".exe", "").Trim();
            if (n.Length == 0) { await SendHtml(cid, "Enter process name.").ConfigureAwait(false); return; }
            if (SelfProtection.IsProtectedProcess(n))
            {
                await SendHtml(cid, $"🛡 Protected system process <code>{Esc(n)}</code> — refused.").ConfigureAwait(false);
                return;
            }
            var procs = Process.GetProcessesByName(n);
            if (procs.Length == 0)
            {
                await SendHtml(cid, $"🔍 Not found: <code>{Esc(n)}</code>").ConfigureAwait(false);
                return;
            }
            foreach (var p in procs) { try { p.Kill(); } catch { } p.Dispose(); }
            await SendHtml(cid, $"✅ Terminated <b>{Esc(n)}</b> ({procs.Length}).").ConfigureAwait(false);
        }
        catch (Exception ex) { await SendHtml(cid, $"⚠️ Terminate error: <code>{Esc(Trim(ex.Message, 200))}</code>").ConfigureAwait(false); }
    }

    private async Task SendWelcome(long cid)
    {
        var kb = MainKeyboard();
        await _botClient!.SendTextMessageAsync(cid,
            $"🛡 <b>SystemGuard Remote</b>\n🖥 <code>{Esc(Environment.MachineName)}</code>\n\n" +
            "Pick a button below or open the <b>Web App</b> for the full dashboard.\n" +
            "<i>/help — all commands</i>",
            parseMode: ParseMode.Html, replyMarkup: kb).ConfigureAwait(false);
    }

    private static ReplyKeyboardMarkup MainKeyboard() => new(new[]
    {
        new[] { new KeyboardButton("🖥 Status"), new KeyboardButton("📊 Processes"), new KeyboardButton("📱 Apps") },
        new[] { new KeyboardButton("🔊 Volume"), new KeyboardButton("🔆 Brightness"), new KeyboardButton("📸 Screenshot") },
        new[] { new KeyboardButton("📡 Stream"), new KeyboardButton("📷 Cam"), new KeyboardButton("📂 Files") },
        new[] { new KeyboardButton("🔌 Shutdown"), new KeyboardButton("🔄 Restart"), new KeyboardButton("🌙 Sleep") },
        new[] { new KeyboardButton("🧹 Clean"), new KeyboardButton("⚡ Optimize RAM"), new KeyboardButton("💻 CMD") },
        new[] { new KeyboardButton("🚀 Open App"), new KeyboardButton("❌ Close App"), new KeyboardButton("🔒 Lock") },
        new[] { new KeyboardButton("⭐ License"), new KeyboardButton("❓ Help") },
        new[] { new KeyboardButton("🌐 Web App") { WebApp = new WebAppInfo { Url = "https://marik1337leet.github.io/systemguard-miniapp" } } }
    })
    { ResizeKeyboard = true };

    private async Task SendMainMenu(long cid)
    {
        try
        {
            await _botClient!.SendTextMessageAsync(cid, "⌨️ <i>Choose an action:</i>",
                parseMode: ParseMode.Html, replyMarkup: MainKeyboard()).ConfigureAwait(false);
        }
        catch { }
    }

    private async Task SendHelp(long cid) => await SendHtml(cid,
        "📖 <b>Commands</b>\n\n" +
        "🖥 <code>/status</code> — system card\n" +
        "📊 <code>/processes</code> • 📱 <code>/apps</code>\n" +
        "📸 <code>/screenshot</code> • 📡 <code>/stream</code> • <code>/stop</code> • 📷 <code>/cam</code>\n" +
        "📂 <code>/ls C:\\</code> • <code>/get C:\\file</code>\n" +
        "🔌 <code>/shutdown</code> • 🔄 <code>/restart</code> • <code>/cancel</code>\n" +
        "🌙 <code>/sleep</code> • 🔒 <code>/lock</code>\n" +
        "🚀 <code>/open chrome</code> • ❌ <code>/close notepad</code>\n" +
        "💻 <code>/cmd ipconfig</code>\n" +
        "🔊 <code>/volume 70</code> • 🔆 <code>/brightness 70</code> • ⏯ <code>/play /next /prev /mute</code>\n" +
        "🧹 <code>/clean</code> • ⚡ <code>/optimize_ram</code>\n" +
        "🌐 <code>/ip</code> • 📶 <code>/ping</code> • ⏱ <code>/uptime</code>\n" +
        "🔋 <code>/battery</code> • 💾 <code>/free</code> • 👥 <code>/users</code>\n" +
        "⭐ <code>/license</code>\n\n" +
        "<i>Tip: arguments work in one line, e.g. <code>/ls C:\\Games</code></i>").ConfigureAwait(false);

    // ── Файлы ───────────────────────────────────────────────────────────────────
    private async Task ListDir(long cid, string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir))
                dir = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            dir = dir.Trim().Trim('"');
            if (SelfProtection.IsProtectedPath(dir) && dir.TrimEnd('\\').Equals(SelfProtection.DataDirectory, StringComparison.OrdinalIgnoreCase))
            {
                await SendHtml(cid, "🛡 SystemGuard data folder is hidden.").ConfigureAwait(false);
                return;
            }
            if (!Directory.Exists(dir))
            {
                await SendHtml(cid, $"📁 Not a folder: <code>{Esc(dir)}</code>").ConfigureAwait(false);
                return;
            }
            var entries = Directory.GetFileSystemEntries(dir).Take(30)
                .Select(e =>
                {
                    try
                    {
                        if (Directory.Exists(e)) return "📁 <code>" + Esc(Path.GetFileName(e)) + "</code>/";
                        var fi = new FileInfo(e);
                        return "📄 <code>" + Esc(Path.GetFileName(e)) + "</code> <i>" + fi.Length / 1024 + " KB</i>";
                    }
                    catch { return "📄 <code>" + Esc(Path.GetFileName(e)) + "</code>"; }
                });
            await SendHtml(cid, $"📁 <code>{Esc(dir)}</code>\n{string.Join("\n", entries)}").ConfigureAwait(false);
        }
        catch (Exception ex) { await SendHtml(cid, $"⚠️ List error: <code>{Esc(Trim(ex.Message, 300))}</code>").ConfigureAwait(false); }
    }

    private async Task SendFileToChat(long cid, string path)
    {
        try
        {
            path = path.Trim().Trim('"');
            if (!File.Exists(path))
            {
                await SendHtml(cid, "❌ File not found.").ConfigureAwait(false);
                return;
            }
            var info = new FileInfo(path);
            if (info.Length > 49L * 1024 * 1024)
            {
                await SendHtml(cid, "❌ File over 50 MB (Bot API limit).").ConfigureAwait(false);
                return;
            }
            await using var s = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            await ThrottledSend(cid, (c, ct) =>
                _botClient!.SendDocumentAsync(c, InputFile.FromStream(s, info.Name),
                    caption: $"📄 <b>{Esc(info.Name)}</b> ({info.Length / 1024} KB)",
                    parseMode: ParseMode.Html, cancellationToken: ct)).ConfigureAwait(false);
        }
        catch (Exception ex) { await SendHtml(cid, $"⚠️ Send error: <code>{Esc(Trim(ex.Message, 300))}</code>").ConfigureAwait(false); }
    }

    // ── Скриншот/стрим ──────────────────────────────────────────────────────────
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int nIndex);
    private const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77, SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;

    private static byte[] CaptureScreenJpeg(int maxWidth = 1120, int quality = 50)
    {
        int vx, vy, vw, vh;
        try
        {
            vx = GetSystemMetrics(SM_XVIRTUALSCREEN);
            vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
            vw = GetSystemMetrics(SM_CXVIRTUALSCREEN);
            vh = GetSystemMetrics(SM_CYVIRTUALSCREEN);
            if (vw <= 0 || vh <= 0) { vx = 0; vy = 0; vw = 1920; vh = 1080; }
        }
        catch { vx = 0; vy = 0; vw = 1920; vh = 1080; }

        double scale = vw > maxWidth ? (double)maxWidth / vw : 1.0;
        int w = Math.Max(320, (int)(vw * scale));
        int h = Math.Max(200, (int)(vh * scale));

        using var bmp = new System.Drawing.Bitmap(w, h);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
            g.CopyFromScreen(vx, vy, 0, 0, new System.Drawing.Size(w, h),
                System.Drawing.CopyPixelOperation.SourceCopy);

        var enc = System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders()
            .First(c => c.MimeType == "image/jpeg");
        var prm = new System.Drawing.Imaging.EncoderParameters(1);
        prm.Param[0] = new System.Drawing.Imaging.EncoderParameter(
            System.Drawing.Imaging.Encoder.Quality, quality);
        using var ms = new MemoryStream();
        bmp.Save(ms, enc, prm);
        return ms.ToArray();
    }

    private readonly Dictionary<long, CancellationTokenSource> _streams = new();
    private readonly object _streamLock = new();

    private void StartScreenStream(long cid)
    {
        lock (_streamLock)
        {
            StopScreenStream(cid);
            var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5)); // автостоп от флуда
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
                byte[]? jpg = null;
                try { jpg = await Task.Run(() => CaptureScreenJpeg(), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                catch { jpg = null; }
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
                            caption: "📡 <i>Live — /stop to end</i>", parseMode: ParseMode.Html,
                            cancellationToken: tok)).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
                catch { /* флуд/таймаут — просто ждём следующий кадр */ }
                try { await Task.Delay(2500, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
            }
        }
        catch (OperationCanceledException) { }
        finally { lock (_streamLock) _streams.Remove(cid); }
    }

    // ── Вебкамера (best-effort) ─────────────────────────────────────────────────
    [DllImport("avicap32.dll", CharSet = CharSet.Ansi)]
    private static extern IntPtr capCreateCaptureWindowA(string name, int style, int x, int y, int w, int h, IntPtr parent, int id);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern IntPtr SendMessage(IntPtr h, int msg, int w, string? l);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr GetDesktopWindow();
    private const int WM_CAP_DRIVER_CONNECT = 0x40A, WM_CAP_DRIVER_DISCONNECT = 0x40B, WM_CAP_FILE_SAVEDIB = 0x415;
    private const int WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000;

    private static string? CaptureWebcam(string outPath)
    {
        IntPtr hwnd = IntPtr.Zero;
        try
        {
            hwnd = capCreateCaptureWindowA("sgcap", WS_CHILD | WS_VISIBLE, 0, 0, 640, 480, GetDesktopWindow(), 0);
            if (hwnd == IntPtr.Zero) return null;
            if (SendMessage(hwnd, WM_CAP_DRIVER_CONNECT, 0, null) == IntPtr.Zero) return null;
            if (SendMessage(hwnd, WM_CAP_FILE_SAVEDIB, 0, outPath) == IntPtr.Zero) return null;
            return File.Exists(outPath) ? outPath : null;
        }
        catch { return null; }
        finally
        {
            try
            {
                if (hwnd != IntPtr.Zero)
                {
                    SendMessage(hwnd, WM_CAP_DRIVER_DISCONNECT, 0, null);
                    DestroyWindow(hwnd);
                }
            }
            catch { }
        }
    }

    private async Task SendWebcamToChat(long cid)
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"sgcam_{Guid.NewGuid():N}.bmp");
        try
        {
            await SendHtml(cid, "📷 <i>Capturing camera…</i>").ConfigureAwait(false);
            var shot = await Task.Run(() => CaptureWebcam(tmp)).ConfigureAwait(false);
            if (shot == null)
            {
                await SendHtml(cid, "📷 Camera unavailable (no webcam / busy / no driver).").ConfigureAwait(false);
                return;
            }
            await using var s = File.Open(shot, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            await ThrottledSend(cid, (c, ct) =>
                _botClient!.SendPhotoAsync(c, InputFile.FromStream(s, "cam.bmp"),
                    caption: "📷 <b>Webcam</b>", parseMode: ParseMode.Html, cancellationToken: ct)).ConfigureAwait(false);
        }
        catch (Exception ex) { await SendHtml(cid, $"⚠️ Camera error: <code>{Esc(Trim(ex.Message, 200))}</code>").ConfigureAwait(false); }
        finally { try { File.Delete(tmp); } catch { } }
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
        await SendHtml(cid, "📱 <b>Running apps</b>\n" +
            string.Join("\n", procs.Select(p => $"• <code>{Esc(p.t)}</code> — {Esc(p.mem)}"))).ConfigureAwait(false);
    }

    private static async Task<string> Exec(string cmd)
    {
        try
        {
            using var p = new Process
            {
                StartInfo = new ProcessStartInfo("cmd.exe", $"/c {cmd}")
                {
                    RedirectStandardOutput = true, RedirectStandardError = true,
                    UseShellExecute = false, CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
                }
            };
            p.Start();
            var oTask = p.StandardOutput.ReadToEndAsync();
            var eTask = p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync().ConfigureAwait(false);
            var o = await oTask.ConfigureAwait(false);
            var e = await eTask.ConfigureAwait(false);
            return string.IsNullOrEmpty(o) ? e : o;
        }
        catch (Exception ex) { return ex.Message; }
    }

    private async Task ExecuteAndSend(long cid, string cmd)
    {
        await SendHtml(cid, $"💻 <code>{Esc(Trim(cmd, 200))}</code>\n<i>Running…</i>").ConfigureAwait(false);
        var r = await Exec(cmd).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(r)) r = "(no output)";
        if (r.Length > 3800) r = r[..3800] + "\n…(truncated)";
        await SendHtml(cid, $"💻 <code>{Esc(Trim(cmd, 200))}</code>\n<pre>{Esc(r)}</pre>").ConfigureAwait(false);
    }

    // ── Шлюз отправки ───────────────────────────────────────────────────────────
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
            markup = null; // клавиатура только в первом чанке
        }
    }

    private Task SendMsg(long cid, string msg)
    {
        var t = string.IsNullOrWhiteSpace(msg) ? "(empty)" : msg.Trim();
        if (t.Length > 3800) t = t[..3800] + "\n…(truncated)";
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

    private async Task HandleFileUpload(Message msg)
    {
        var c = msg.Chat.Id;
        try
        {
            var dp = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "TG Downloads");
            Directory.CreateDirectory(dp);
            if (msg.Document != null)
            {
                await SendHtml(c, "📥 <i>Downloading…</i>").ConfigureAwait(false);
                var f = await _botClient!.GetFileAsync(msg.Document.FileId).ConfigureAwait(false);
                var safe = string.Concat((msg.Document.FileName ?? "file").Split(Path.GetInvalidFileNameChars()));
                var p = Path.Combine(dp, string.IsNullOrWhiteSpace(safe) ? "file" : safe);
                var bytes = await _http.GetByteArrayAsync($"https://api.telegram.org/file/bot{_token}/{f.FilePath}").ConfigureAwait(false);
                await File.WriteAllBytesAsync(p, bytes).ConfigureAwait(false);
                await SendHtml(c, $"✅ Saved to PC:\n<code>{Esc(p)}</code>").ConfigureAwait(false);
            }
            else await SendHtml(c, "📷 Photo received — send as <b>file/document</b> to save it on PC.").ConfigureAwait(false);
        }
        catch (Exception ex) { await SendHtml(c, $"⚠️ Upload error: <code>{Esc(Trim(ex.Message, 200))}</code>").ConfigureAwait(false); }
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
                        await SendMessage($"⚠️ Warning: low disk space on {d.Name}").ConfigureAwait(false);
                }
                await Task.Delay(TimeSpan.FromMinutes(5), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch { try { await Task.Delay(TimeSpan.FromMinutes(5), ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; } }
        }
    }

    private async Task ShowLicenseMenu(long cid) => await _botClient!.SendTextMessageAsync(cid,
        "⭐ <b>SystemGuard Pro</b>\n\n" +
        "📅 Monthly — <b>300 ★</b>\n" +
        "🗓 Half-Year — <b>1000 ★</b>\n" +
        "🎆 Yearly — <b>1800 ★</b>\n" +
        "💎 Lifetime — <b>3000 ★</b>\n\n" +
        "<i>After payment the bot sends an activation key — paste it in the app (License tab).</i>",
        parseMode: ParseMode.Html,
        replyMarkup: new InlineKeyboardMarkup(new[]
        {
            new[] { InlineKeyboardButton.WithCallbackData("📅 Monthly — 300 ★", "buy_monthly") },
            new[] { InlineKeyboardButton.WithCallbackData("🗓 Half-Year — 1000 ★", "buy_halfyear") },
            new[] { InlineKeyboardButton.WithCallbackData("🎆 Yearly — 1800 ★", "buy_yearly") },
            new[] { InlineKeyboardButton.WithCallbackData("💎 Lifetime — 3000 ★", "buy_lifetime") },
        })).ConfigureAwait(false);

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

    private async Task SendInvoice(long cid, string n, int s, int m)
    {
        try
        {
            await _botClient!.SendInvoiceAsync(cid, $"SG Pro — {n}", $"Pro license: {n.ToLower()}",
                $"{n}|{m}", "XTR", new[] { new LabeledPrice($"Pro {n}", s) }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await SendHtml(cid, $"⚠️ Invoice failed: <code>{Esc(Trim(ex.Message, 200))}</code>").ConfigureAwait(false);
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
            await _botClient!.SendTextMessageAsync(msg.Chat.Id,
                $"✅ <b>Paid! Thank you!</b>\n\nActivate in the app (License tab):\n🔑 Key: <code>{GenerateKey("Pro", exp, plan)}</code>\n📅 Valid until {exp:dd MMM yyyy}",
                parseMode: ParseMode.Html).ConfigureAwait(false);
        }
        catch { }
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

    private async Task SendScreenshotToChat(long cid)
    {
        await SendHtml(cid, "📸 <i>Capturing screen…</i>").ConfigureAwait(false);
        var jpg = await Task.Run(() =>
        {
            try { return CaptureScreenJpeg(); }
            catch { return null; }
        }).ConfigureAwait(false);
        if (jpg == null)
        {
            await SendHtml(cid, "📸 Screenshot failed (no desktop / driver error).").ConfigureAwait(false);
            return;
        }
        try
        {
            using var ms = new MemoryStream(jpg);
            await ThrottledSend(cid, (c, ct) =>
                _botClient!.SendPhotoAsync(c, InputFile.FromStream(ms, "ss.jpg"),
                    caption: $"📸 <b>{Esc(Environment.MachineName)}</b> • {DateTime.Now:HH:mm:ss}",
                    parseMode: ParseMode.Html, cancellationToken: ct)).ConfigureAwait(false);
        }
        catch { await SendHtml(cid, "📸 Screenshot upload failed.").ConfigureAwait(false); }
    }

    public async Task<string> SendScreenshot()
    {
        try
        {
            if (_botClient == null || !IsConfigured) return "ERR: bot not configured";
            if (!long.TryParse(_chatId, out var id)) return "ERR: bad chat id";
            var jpg = await Task.Run(() =>
            {
                try { return CaptureScreenJpeg(); }
                catch { return null; }
            }).ConfigureAwait(false);
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

    // ── Публичное API для вкладки Telegram ──────────────────────────────────────
    public bool IsStreaming { get { lock (_streamLock) return _streams.Count > 0; } }

    public string StartStreamToConfigured()
    {
        if (!IsConfigured || !long.TryParse(_chatId, out var id)) return "Connect bot first";
        StartScreenStream(id);
        return "Live stream started";
    }

    public string StopAllStreams()
    {
        foreach (var id in _streams.Keys.ToList()) StopScreenStream(id);
        return "All streams stopped";
    }

    public Task SendWebcamToConfigured() =>
        IsConfigured && long.TryParse(_chatId, out var id)
            ? SendWebcamToChat(id) : Task.CompletedTask;

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
