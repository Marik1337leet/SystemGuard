// SystemGuard.ShopBot — отдельный бот-магазин (оплата Stars + криптоплатежка).
// Запускать ТОЛЬКО на сервере / твоём ПК. Никогда не включать в дистрибутив Desktop.
//
// Токен НЕ в коде: переменная окружения SHOP_BOT_TOKEN или файл
// %LocalAppData%\SystemGuard\shopbot.json (см. shopbot.example.json).
// Supabase: тот же файл (supabaseUrl + serviceKey). Без них — локальный shop_data.json.
//
// Поток продажи:
//   1. Приложение открывает https://t.me/<shopbot>?start=buy_<HWID> (HWID этого ПК).
//   2. Пользователь жмёт тариф → счёт Stars или криптоплатежка.
//   3. После оплаты бот генерирует ключ, ПРИВЯЗАННЫЙ к HWID, пишет в БД
//      и присылает ключ + кнопку «как активировать».
//   4. Приложение: кнопка «Я оплатил, проверить» сама забирает ключ по HWID
//      из Supabase и активирует БЕЗ показа ключа. Подарочные ключи (розыгрыши)
//      вводятся вручную в Settings → License → «подарочный ключ».

using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.Payments;
using Telegram.Bot.Types.ReplyMarkups;

var cfg = ShopConfig.Load();
if (string.IsNullOrWhiteSpace(cfg.BotToken))
{
    Console.WriteLine("SHOP_BOT_TOKEN пуст. Задай переменную окружения SHOP_BOT_TOKEN");
    Console.WriteLine(@"или заполни %LocalAppData%\SystemGuard\shopbot.json по shopbot.example.json");
    return 1;
}

var bot = new TelegramBotClient(cfg.BotToken);
var store = ShopStoreFactory.Create(cfg);
var keys = new LicenseKeyService();
var crypto = CryptoPayFactory.Create(cfg);
var pendingCrypto = new PendingCryptoStore();

Console.WriteLine($"ShopBot started as @{(await bot.GetMe()).Username}, store={store.Name}, crypto={crypto.Name}");

// Служебный прогон выдачи без денег: dotnet SystemGuard.ShopBot.dll --selftest <chatId>
// Шлёт тестовый ключ (план Test, срок 0 дней — сразу истёкший, злоупотребить нельзя)
// и пишет строку в БД. После проверки строку удалить вручную.
if (args.Any(a => a == "--selftest"))
{
    var chatId = args.SkipWhile(a => a != "--selftest").Skip(1)
        .Select(a => long.TryParse(a, out var id) ? id : 0).FirstOrDefault();
    if (chatId == 0) chatId = cfg.OwnerId;
    if (chatId == 0) { Console.WriteLine("selftest: no chat id and ownerId=0"); return 2; }
    await Delivery.PaidKey(bot, store, keys, cfg, chatId, chatId, "selftest", "test:selftest", "Test|0", CancellationToken.None);
    Console.WriteLine("selftest done");
    return 0;
}

using var cts = new CancellationTokenSource();
var opts = new ReceiverOptions { AllowedUpdates = new[] { UpdateType.Message, UpdateType.CallbackQuery, UpdateType.PreCheckoutQuery } };
bot.StartReceiving(
    async (c, u, ct) => await HandleAsync(bot, store, keys, crypto, pendingCrypto, cfg, c, u, ct),
    async (c, ex, ct) => { Console.WriteLine($"Poll error: {ex.Message}"); await Task.CompletedTask; },
    opts, cts.Token);
// Фон: проверка криптосчетов каждые 30 сек — оплачен → ключ сам прилетает в личку.
_ = Task.Run(() => CryptoPoller.LoopAsync(bot, store, keys, pendingCrypto, cfg, cts.Token));

Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
try
{
    if (!Console.IsInputRedirected)
    {
        try
        {
            Console.WriteLine("Press Enter to stop.");
            while (!cts.Token.IsCancellationRequested)
            {
                if (Console.KeyAvailable) { Console.ReadLine(); break; }
                await Task.Delay(300, cts.Token);
            }
        }
        catch (IOException)
        {
            // Консоли нет вообще (фоновый процесс): живём пока не убьют.
            Console.WriteLine("Running headless until process is killed.");
            await Task.Delay(Timeout.InfiniteTimeSpan, cts.Token);
        }
    }
    else
    {
        // Фоновый запуск (stdin перенаправлен): живём пока не убьют процесс.
        Console.WriteLine("Running headless until process is killed.");
        await Task.Delay(Timeout.InfiniteTimeSpan, cts.Token);
    }
}
catch (OperationCanceledException) { }
cts.Cancel();
return 0;

static async Task HandleAsync(TelegramBotClient bot, IShopStore store, LicenseKeyService keys, ICryptoPay crypto, PendingCryptoStore pending, ShopConfig cfg, ITelegramBotClient c, Update u, CancellationToken ct)
{
    try
    {
        if (u.PreCheckoutQuery != null)
        {
            await bot.AnswerPreCheckoutQuery(u.PreCheckoutQuery.Id, cancellationToken: ct);
            return;
        }
        if (u.CallbackQuery is { } q)
        {
            await bot.AnswerCallbackQuery(q.Id, cancellationToken: ct);
            var d = q.Data ?? "";
            var chat = q.Message!.Chat.Id;
            if (d.StartsWith("plan_"))
                await SendPlanCard(bot, chat, Plans.ByKey(d["plan_".Length..]), ct);
            else if (d.StartsWith("stars_"))
                await SendStarsInvoice(bot, chat, Plans.ByKey(d["stars_".Length..]), ct);
            else if (d.StartsWith("crypto_"))
                await SendCryptoInvoice(bot, crypto, pending, chat, q.From, Plans.ByKey(d["crypto_".Length..]), ct);
            else if (d == "shop")
                await SendBuyMenu(bot, chat, ct);
            else if (d.StartsWith("buy_"))
                // Старые сообщения (до карточек): сразу счёт Stars.
                await SendStarsInvoice(bot, chat, Plans.ByKey(d["buy_".Length..]), ct);
            else if (d.StartsWith("pay_crypto_"))
                await SendCryptoInvoice(bot, crypto, pending, chat, q.From, Plans.ByKey(d["pay_crypto_".Length..]), ct);
            else if (d == "tariffs")
                await SendTariffs(bot, q.Message!.Chat.Id, ct);
            return;
        }
        if (u.Message is not { } m) return;

        if (m.SuccessfulPayment is { } p)
        {
            await DeliverPaidKey(bot, store, keys, cfg, m.Chat.Id, m.From?.Id ?? m.Chat.Id, m.From?.Username, $"stars:{p.TotalAmount}", p.InvoicePayload, ct);
            return;
        }
        var text = (m.Text ?? "").Trim();
        if (text.StartsWith("/start"))
        {
            var arg = text.Contains(' ') ? text[(text.IndexOf(' ') + 1)..].Trim() : "";
            if (arg.StartsWith("buy_", StringComparison.OrdinalIgnoreCase))
                store.BindHwid(m.From?.Id ?? m.Chat.Id, arg["buy_".Length..].Trim());
            await SendWelcome(bot, m.Chat.Id, ct);
        }
        else if (text is "/help" or "/menu") await SendWelcome(bot, m.Chat.Id, ct);
        else if (text is "/tariffs" or "/tariff" or "/prices" or "/pro") await SendTariffs(bot, m.Chat.Id, ct);
        else if (text is "/buy" or "/license") await SendBuyMenu(bot, m.Chat.Id, ct);
        else await SendWelcome(bot, m.Chat.Id, ct);
    }
    catch (Exception ex) { Console.WriteLine($"Handle error: {ex.Message}"); }
}

static async Task SendWelcome(ITelegramBotClient bot, long chat, CancellationToken ct) =>
    await bot.SendMessage(chat,
        "<b>SystemGuard Pro — магазин</b>\n\n" +
        "Здесь покупают Pro-лицензию для приложения SystemGuard.\n" +
        "Оплата: <b>Telegram Stars</b> или <b>криптоплатежка</b>.\n\n" +
        "<b>Как купить:</b>\n" +
        "1. Нажми <b>Купить</b> ниже и выбери тариф.\n" +
        "2. Оплати счёт.\n" +
        "3. Бот выдаст ключ, привязанный к твоему ПК.\n" +
        "4. В приложении нажми <b>«Я оплатил, проверить»</b> — ключ подхватится сам.\n\n" +
        "Вопросы и проблемы с оплатой: @mattrix_solution\n\n" +
        "<i><code>/tariffs</code> — что входит во Free и Pro • <code>/buy</code> — купить</i>",
        ParseMode.Html,
        replyMarkup: new InlineKeyboardMarkup(new[]
        {
            new[] { InlineKeyboardButton.WithCallbackData("Купить Pro", "tariffs") },
            new[] { InlineKeyboardButton.WithCallbackData("Тарифы: Free vs Pro", "tariffs") },
        }), cancellationToken: ct);

static async Task SendTariffs(ITelegramBotClient bot, long chat, CancellationToken ct) =>
    await bot.SendMessage(chat,
        "<b>SystemGuard — тарифы</b>\n\n" +
        "<b>FREE (навсегда)</b>\n" +
        "• Мониторинг CPU/GPU/RAM/сети/дисков, история, Free Memory, Flush DNS\n" +
        "• Процессы: просмотр, Kill • Автозагрузка: просмотр, Disable\n" +
        "• Очистка: Scan, базовая Clean, корзина • Питание: Shutdown/Restart/Sleep/Lock\n" +
        "• Defender: Quick Scan, Status\n\n" +
        "<b>PRO</b>\n" +
        "• Процессы: Force Kill, дерево, Suspend/Resume, дампы, VirusTotal, приоритеты\n" +
        "• Службы Start/Stop • Глубокое удаление программ • Игровой режим + твики\n" +
        "• Бенчмарки и стресс • Сеть: DNS, hosts, сканер портов/Wi-Fi, Wake-on-LAN\n" +
        "• Шифрование файлов, менеджер паролей • Расширенный Telegram-пульт и live-ссылка\n" +
        "• Планировщик задач\n\n" +
        "<b>Цены:</b>\n" +
        "• Monthly — 300 Stars / 4 USDT\n" +
        "• Half-Year — 1000 Stars / 12 USDT\n" +
        "• Yearly — 1800 Stars / 20 USDT\n" +
        "• Lifetime — 3000 Stars / 35 USDT\n\n" +
        "Купить: <code>/buy</code>. Триал 14 дней — в приложении (Settings → License).",
        ParseMode.Html, cancellationToken: ct);

static async Task SendBuyMenu(ITelegramBotClient bot, long chat, CancellationToken ct) =>
    await bot.SendMessage(chat,
        "<b>Магазин Pro — выбери тариф:</b>\n<i>Нажми на тариф, чтобы увидеть карточку и оплатить.</i>",
        ParseMode.Html,
        replyMarkup: new InlineKeyboardMarkup(new[]
        {
            new[] { InlineKeyboardButton.WithCallbackData("Monthly — 300 ★ / 4 USDT", "plan_monthly") },
            new[] { InlineKeyboardButton.WithCallbackData("Half-Year — 1000 ★ / 12 USDT", "plan_halfyear") },
            new[] { InlineKeyboardButton.WithCallbackData("Yearly — 1800 ★ / 20 USDT", "plan_yearly") },
            new[] { InlineKeyboardButton.WithCallbackData("Lifetime — 3000 ★ / 35 USDT", "plan_lifetime") },
        }), cancellationToken: ct);

/// <summary>Карточка тарифа: что входит, срок, обе цены, обе кнопки оплаты.</summary>
static async Task SendPlanCard(ITelegramBotClient bot, long chat, PlanInfo p, CancellationToken ct)
{
    var key = Plans.Key(p.Name);
    await bot.SendMessage(chat,
        $"<b>Pro {p.Name}</b> — {p.Days}\n\n" +
        $"{p.Blurb}\n\n" +
        "Входит: глубокое удаление программ, игровой режим, планировщик, " +
        "бенчмарки, расширенная сеть, шифрование файлов, расширенный Telegram-пульт и live-доступ.\n\n" +
        $"Цена: <b>{p.Stars} Stars</b> или <b>{p.Usdt} USDT</b> (TON, BTC и другие — на странице счёта).\n" +
        "<i>Ключ привязывается к твоему ПК автоматически.</i>",
        ParseMode.Html,
        replyMarkup: new InlineKeyboardMarkup(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData($"Оплатить {p.Stars} ★", $"stars_{key}"),
                InlineKeyboardButton.WithCallbackData($"Криптой {p.Usdt} USDT", $"crypto_{key}"),
            },
            new[] { InlineKeyboardButton.WithCallbackData("← Все тарифы", "shop") },
        }), cancellationToken: ct);
}

static async Task SendStarsInvoice(ITelegramBotClient bot, long chat, PlanInfo p, CancellationToken ct)
{
    try
    {
        await bot.SendInvoice(chat, $"SG Pro — {p.Name}", $"Pro license: {p.Name.ToLower()} ({p.Days})",
            $"{p.Name}|{p.Months}", "XTR", new[] { new LabeledPrice($"Pro {p.Name}", p.Stars) },
            cancellationToken: ct);
    }
    catch (Exception ex) { await bot.SendMessage(chat, $"Счёт не создался: {ex.Message}", cancellationToken: ct); }
}

static async Task SendCryptoInvoice(ITelegramBotClient bot, ICryptoPay crypto, PendingCryptoStore pending, long chat, User? from, PlanInfo p, CancellationToken ct)
{
    var inv = await crypto.CreateInvoiceAsync(p, from?.Id ?? chat, ct);
    if (inv == null)
    {
        await bot.SendMessage(chat,
            "Криптосчёт не создался. Напиши @mattrix_solution — выдадут счёт вручную.",
            cancellationToken: ct);
        return;
    }
    pending.Add(inv.Id, chat, from?.Id ?? chat, from?.Username, p.Name);
    await bot.SendMessage(chat,
        $"<b>Pro {p.Name} — {p.Usdt} USDT</b>\n\n" +
        $"Оплати счёт (можно TON, BTC и другими — выбор на странице оплаты):\n" +
        $"<a href=\"{inv.Url}\">Открыть счёт</a>\n\n" +
        "<i>Счёт действует 1 час. После оплаты ключ прилетит сюда сам — никуда нажимать не нужно.</i>",
        ParseMode.Html, cancellationToken: ct);
}

static async Task DeliverPaidKey(TelegramBotClient bot, IShopStore store, LicenseKeyService keys, ShopConfig cfg, long chat, long telegramId, string? username, string paymentRef, string payload, CancellationToken ct)
    => await Delivery.PaidKey(bot, store, keys, cfg, chat, telegramId, username, paymentRef, payload, ct);

// ── Выдача ключей после оплаты (Stars и крипта идут сюда одной дорогой) ──
static class Delivery
{
    public static async Task PaidKey(TelegramBotClient bot, IShopStore store, LicenseKeyService keys, ShopConfig cfg, long chat, long telegramId, string? username, string paymentRef, string payload, CancellationToken ct)
    {
        try
        {
            var parts = payload.Split('|');
            var planName = parts[0];
            var months = int.Parse(parts[1]);
            var exp = months >= 999 ? DateTime.Now.AddYears(99) : DateTime.Now.AddMonths(months);
            var plan = months >= 999 ? "Lifetime" : planName;
            var hwid = store.GetHwid(telegramId);
            var key = keys.Generate("Pro", exp, plan, hwid);
            // Клиент обязан существовать (FK licenses→customers): прямые заходы
            // в бота без start=buy_HWIID иначе роняли бы выдачу с 409.
            await store.EnsureCustomerAsync(telegramId, username, hwid, ct).ConfigureAwait(false);
            await store.SaveLicenseAsync(new LicenseRecord(
                TelegramId: telegramId,
                Username: username,
                Hwid: hwid,
                Tier: "Pro", Plan: plan,
                ExpiresAt: exp, KeyPrefix: key[..12],
                PaymentRef: paymentRef, CreatedAt: DateTime.UtcNow), ct);
            await bot.SendMessage(chat,
                $"<b>Оплата прошла. Спасибо!</b>\n\n" +
                $"Тариф: <b>Pro {plan}</b>, до {exp:dd MMM yyyy}.\n" +
                (string.IsNullOrEmpty(hwid)
                    ? "Вернись в приложение и вставь ключ (Settings → License → «подарочный ключ»):\n"
                    : "Ключ уже привязан к твоему ПК — в приложении нажми <b>«Я оплатил, проверить»</b>:\n") +
                $"<tg-spoiler><code>{key}</code></tg-spoiler>",
                ParseMode.Html, cancellationToken: ct);
            if (cfg.OwnerId != 0)
                try
                {
                    await bot.SendMessage(cfg.OwnerId,
                        $"<b>Продажа Pro {plan}</b> ({paymentRef})\n" +
                        $"От: @{(username ?? "?")} (id <code>{telegramId}</code>)\n" +
                        $"HWID: <code>{(string.IsNullOrEmpty(hwid) ? "—" : hwid)}</code>\n" +
                        $"Ключ: <code>{key}</code>",
                        ParseMode.Html, cancellationToken: ct);
                }
                catch { }
        }
        catch (Exception ex) { Console.WriteLine($"Deliver error: {ex.Message}"); }
    }
}

// ── Тарифы: ЕДИНСТВЕННОЕ место цен. Stars + USDT рядом. ──
sealed record PlanInfo(string Name, int Stars, int Months, string Usdt, string Days, string Blurb);

static class Plans
{
    public static readonly Dictionary<string, PlanInfo> All = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Monthly"] = new("Monthly", 300, 1, "4", "30 дней",
            "Познакомиться с Pro: все функции на месяц."),
        ["Half-Year"] = new("Half-Year", 1000, 6, "12", "182 дня",
            "Полгода Pro. Выгоднее помесячной в 1.8 раза."),
        ["Yearly"] = new("Yearly", 1800, 12, "20", "365 дней",
            "Год Pro. Самый популярный: дешевле двух помесячных."),
        ["Lifetime"] = new("Lifetime", 3000, 999, "35", "навсегда",
            "Один платёж — Pro навсегда, все будущие обновления."),
    };

    public static string Key(string name) => name.ToLower().Replace("-", "");
    public static PlanInfo ByKey(string key) =>
        All.Values.FirstOrDefault(p => Key(p.Name) == key.ToLower()) ?? All["Monthly"];
}

// ── Конфиг: только файлы/env, никакого хардкода токена ──
sealed class ShopConfig
{
    public string BotToken { get; set; } = "";
    public long OwnerId { get; set; }
    public string SupabaseUrl { get; set; } = "";
    public string SupabaseServiceKey { get; set; } = "";
    public string CryptoPayToken { get; set; } = "";

    public static ShopConfig Load()
    {
        var cfg = new ShopConfig
        {
            BotToken = Environment.GetEnvironmentVariable("SHOP_BOT_TOKEN") ?? ""
        };
        try
        {
            var p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SystemGuard", "shopbot.json");
            if (System.IO.File.Exists(p))
            {
                var j = JsonDocument.Parse(System.IO.File.ReadAllText(p)).RootElement;
                if (string.IsNullOrEmpty(cfg.BotToken) && j.TryGetProperty("botToken", out var t)) cfg.BotToken = t.GetString() ?? "";
                if (j.TryGetProperty("ownerId", out var o)) cfg.OwnerId = o.GetInt64();
                if (j.TryGetProperty("supabaseUrl", out var u)) cfg.SupabaseUrl = u.GetString() ?? "";
                if (j.TryGetProperty("supabaseServiceKey", out var k)) cfg.SupabaseServiceKey = k.GetString() ?? "";
                if (j.TryGetProperty("cryptoPayToken", out var c)) cfg.CryptoPayToken = c.GetString() ?? "";
                var env = Environment.GetEnvironmentVariable("SHOP_BOT_TOKEN");
                if (!string.IsNullOrEmpty(env)) cfg.BotToken = env;
            }
        }
        catch (Exception ex) { Console.WriteLine($"shopbot.json: {ex.Message}"); }
        return cfg;
    }
}

// ── Хранилище: файл сейчас, Supabase когда дашь ключи ──
record LicenseRecord(long TelegramId, string? Username, string Hwid, string Tier, string Plan, DateTime ExpiresAt, string KeyPrefix, string PaymentRef, DateTime CreatedAt);

interface IShopStore
{
    string Name { get; }
    void BindHwid(long telegramId, string hwid);
    string GetHwid(long telegramId);
    Task EnsureCustomerAsync(long telegramId, string? username, string hwid, CancellationToken ct);
    Task SaveLicenseAsync(LicenseRecord r, CancellationToken ct);
}

static class ShopStoreFactory
{
    public static IShopStore Create(ShopConfig cfg) =>
        !string.IsNullOrWhiteSpace(cfg.SupabaseUrl) && !string.IsNullOrWhiteSpace(cfg.SupabaseServiceKey)
            ? new SupabaseShopStore(cfg.SupabaseUrl, cfg.SupabaseServiceKey)
            : new FileShopStore();
}

sealed class FileShopStore : IShopStore
{
    public string Name => "file";
    private readonly string _dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SystemGuard", "shop");
    private readonly Dictionary<long, string> _hwids = new();
    private string HwidsPath => Path.Combine(_dir, "hwids.json");
    public FileShopStore() { LoadHwids(); }
    private void LoadHwids()
    {
        try
        {
            if (!System.IO.File.Exists(HwidsPath)) return;
            var d = JsonSerializer.Deserialize<Dictionary<long, string>>(System.IO.File.ReadAllText(HwidsPath));
            if (d != null) foreach (var (k, v) in d) _hwids[k] = v;
        }
        catch { }
    }
    public void BindHwid(long id, string hwid)
    {
        if (hwid.Length is not 32) return;
        _hwids[id] = hwid.ToLower();
        try { Directory.CreateDirectory(_dir); System.IO.File.WriteAllText(HwidsPath, JsonSerializer.Serialize(_hwids)); } catch { }
    }
    public string GetHwid(long id) => _hwids.TryGetValue(id, out var h) ? h : "";
    public Task EnsureCustomerAsync(long telegramId, string? username, string hwid, CancellationToken ct)
        => Task.CompletedTask; // файлу клиенты не нужны
    public async Task SaveLicenseAsync(LicenseRecord r, CancellationToken ct)
    {
        Directory.CreateDirectory(_dir);
        var line = JsonSerializer.Serialize(r) + Environment.NewLine;
        await System.IO.File.AppendAllTextAsync(Path.Combine(_dir, "sales.jsonl"), line, ct);
    }
}

sealed class SupabaseShopStore(string url, string serviceKey) : IShopStore
{
    public string Name => "supabase";
    private readonly HttpClient _http = new();
    private readonly Dictionary<long, string> _hwids = new();
    public void BindHwid(long id, string hwid)
    {
        if (hwid.Length is not 32) return;
        hwid = hwid.ToLower();
        _hwids[id] = hwid;
        // customers — upsert, чтобы привязка переживала рестарт бота.
        try { EnsureCustomerAsync(id, null, hwid, CancellationToken.None).GetAwaiter().GetResult(); }
        catch (Exception ex) { Console.WriteLine($"customers upsert: {ex.Message}"); }
    }
    public string GetHwid(long id) => _hwids.TryGetValue(id, out var h) ? h : "";

    public async Task EnsureCustomerAsync(long telegramId, string? username, string hwid, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url.TrimEnd('/') + "/rest/v1/customers");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", serviceKey);
            req.Headers.Add("apikey", serviceKey);
            req.Headers.Add("Prefer", "resolution=merge-duplicates");
            req.Content = new StringContent(
                JsonSerializer.Serialize(new { telegram_id = telegramId, username, hwid }),
                Encoding.UTF8, "application/json");
            _http.SendAsync(req).GetAwaiter().GetResult().EnsureSuccessStatusCode();
        }
        catch (Exception ex) { Console.WriteLine($"customers upsert: {ex.Message}"); }
        await Task.CompletedTask;
    }
    public async Task SaveLicenseAsync(LicenseRecord r, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url.TrimEnd('/') + "/rest/v1/licenses");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", serviceKey);
        req.Headers.Add("apikey", serviceKey);
        var body = JsonSerializer.Serialize(new
        {
            telegram_id = r.TelegramId,
            username = r.Username,
            hwid = r.Hwid,
            tier = r.Tier,
            plan = r.Plan,
            expires_at = r.ExpiresAt.ToString("o"),
            key_prefix = r.KeyPrefix,
            payment_ref = r.PaymentRef
        });
        req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        var res = await _http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
    }
}

// ── Генерация ключей. ТОЛЬКО СЕРВЕР (shop-bot). В Desktop-клиент не копировать:
// секрет в открытом клиенте = бесконечная ковка ключей после декомпиляции. ──
sealed class LicenseKeyService
{
    public string Generate(string tier, DateTime expiry, string plan, string hwid)
    {
        var secret = SHA256.HashData(Encoding.UTF8.GetBytes("SystemGuard_License_HMAC_Secret_2025"));
        var data = $"{tier}|{expiry:yyyy-MM-dd}|{plan}|{hwid ?? ""}";
        var db = Encoding.UTF8.GetBytes(data);
        using var hmac = new HMACSHA256(secret);
        var sig = hmac.ComputeHash(db);
        static string B64(byte[] b) => Convert.ToBase64String(b).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        return $"SG-PRO-{B64(db)}.{B64(sig)}";
    }
}

// ── Криптоплатежка: @CryptoBot (Crypto Pay API) ──
sealed record CryptoInvoice(long Id, string Url);

interface ICryptoPay
{
    string Name { get; }
    Task<CryptoInvoice?> CreateInvoiceAsync(PlanInfo plan, long telegramId, CancellationToken ct);
    Task<Dictionary<long, string>> GetPaidInvoiceIdsAsync(IEnumerable<long> ids, CancellationToken ct);
}

static class CryptoPayFactory
{
    public static ICryptoPay Create(ShopConfig cfg) =>
        string.IsNullOrWhiteSpace(cfg.CryptoPayToken) ? new NotConfiguredCryptoPay() : new CryptoBotPay(cfg.CryptoPayToken);
}

sealed class NotConfiguredCryptoPay : ICryptoPay
{
    public string Name => "off";
    public Task<CryptoInvoice?> CreateInvoiceAsync(PlanInfo plan, long telegramId, CancellationToken ct)
        => Task.FromResult<CryptoInvoice?>(null); // бот сам предложит написать владельцу
    public Task<Dictionary<long, string>> GetPaidInvoiceIdsAsync(IEnumerable<long> ids, CancellationToken ct)
        => Task.FromResult(new Dictionary<long, string>());
}

sealed class CryptoBotPay(string token) : ICryptoPay
{
    public string Name => "cryptobot";
    private readonly HttpClient _http = new();
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = null };

    private async Task<JsonDocument?> PostAsync(string method, object body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://pay.crypt.bot/api/" + method);
        req.Headers.Add("Crypto-Pay-API-Token", token);
        req.Headers.UserAgent.ParseAdd("SystemGuardShop/1.0");
        req.Content = new StringContent(JsonSerializer.Serialize(body, JsonOpts), Encoding.UTF8, "application/json");
        using var res = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode) return null;
        try { return JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false)); }
        catch { return null; }
    }

    public async Task<CryptoInvoice?> CreateInvoiceAsync(PlanInfo plan, long telegramId, CancellationToken ct)
    {
        using var doc = await PostAsync("createInvoice", new
        {
            asset = "USDT",
            amount = plan.Usdt,
            description = $"SystemGuard Pro {plan.Name} ({plan.Days})",
            hidden_message = "Спасибо за покупку! Ключ уже летит в чат магазина.",
            paid_btn_name = "openChannel",
            paid_btn_url = "https://t.me/SystemGuardPayBot",
            payload = $"{telegramId}:{plan.Name}",
            expires_in = 3600 // счёт живёт 1 час
        }, ct).ConfigureAwait(false);
        if (doc == null) return null;
        var r = doc.RootElement.GetProperty("result");
        if (!r.TryGetProperty("invoice_id", out var id) || !r.TryGetProperty("bot_invoice_url", out var u))
            return null;
        return new CryptoInvoice(id.GetInt64(), u.GetString() ?? "");
    }

    /// <summary>Возвращает id оплаченных счетов из списка (статус paid).</summary>
    public async Task<Dictionary<long, string>> GetPaidInvoiceIdsAsync(IEnumerable<long> ids, CancellationToken ct)
    {
        var list = ids.ToList();
        var paid = new Dictionary<long, string>();
        if (list.Count == 0) return paid;
        using var doc = await PostAsync("getInvoices", new { invoice_ids = string.Join(",", list) }, ct).ConfigureAwait(false);
        if (doc == null) return paid;
        if (!doc.RootElement.TryGetProperty("result", out var res) || res.ValueKind != JsonValueKind.Object)
            return paid;
        if (!res.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            return paid;
        foreach (var it in items.EnumerateArray())
        {
            if (it.TryGetProperty("status", out var s) && s.GetString() == "paid"
                && it.TryGetProperty("invoice_id", out var id))
                paid[id.GetInt64()] = it.TryGetProperty("payload", out var p) ? p.GetString() ?? "" : "";
        }
        return paid;
    }
}

// ── Ожидающие криптосчета: переживают рестарт (файл), проверяются фоном ──
sealed record PendingCrypto(long InvoiceId, long ChatId, long TelegramId, string? Username, string Plan, DateTime CreatedAt);

sealed class PendingCryptoStore
{
    private readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SystemGuard", "shop", "crypto_pending.json");
    private readonly Dictionary<long, PendingCrypto> _items = new();
    private readonly object _lock = new();

    public PendingCryptoStore() { Load(); }

    private void Load()
    {
        try
        {
            if (!System.IO.File.Exists(_path)) return;
            var list = JsonSerializer.Deserialize<List<PendingCrypto>>(System.IO.File.ReadAllText(_path));
            if (list == null) return;
            // Мусор старше 2 часов не воскрешаем (счета и так протухли).
            foreach (var p in list.Where(p => DateTime.UtcNow - p.CreatedAt < TimeSpan.FromHours(2)))
                _items[p.InvoiceId] = p;
        }
        catch { }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            System.IO.File.WriteAllText(_path, JsonSerializer.Serialize(_items.Values.ToList()));
        }
        catch { }
    }

    public void Add(long invoiceId, long chatId, long telegramId, string? username, string plan)
    {
        lock (_lock)
        {
            _items[invoiceId] = new PendingCrypto(invoiceId, chatId, telegramId, username, plan, DateTime.UtcNow);
            Save();
        }
    }

    public void Remove(long invoiceId)
    {
        lock (_lock) { _items.Remove(invoiceId); Save(); }
    }

    public List<PendingCrypto> Snapshot()
    {
        lock (_lock) return _items.Values.ToList();
    }
}

static class CryptoPoller
{
    /// <summary>Каждые 30 сек спрашиваем CryptoBot: что оплатили → выдаём ключи.</summary>
    public static async Task LoopAsync(TelegramBotClient bot, IShopStore store, LicenseKeyService keys, PendingCryptoStore pending, ShopConfig cfg, CancellationToken ct)
    {
        var crypto = CryptoPayFactory.Create(cfg);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
                var items = pending.Snapshot();
                // Чистим протухшие молча (старше 70 минут).
                foreach (var old in items.Where(i => DateTime.UtcNow - i.CreatedAt > TimeSpan.FromMinutes(70)))
                {
                    pending.Remove(old.InvoiceId);
                    try
                    {
                        await bot.SendMessage(old.ChatId,
                            $"Счёт Pro {old.Plan} истёк (1 час). Нажми /buy — выставлю новый.",
                            cancellationToken: ct).ConfigureAwait(false);
                    }
                    catch { }
                }
                items = pending.Snapshot();
                if (items.Count == 0 || crypto is NotConfiguredCryptoPay) continue;
                var paid = await crypto.GetPaidInvoiceIdsAsync(items.Select(i => i.InvoiceId), ct).ConfigureAwait(false);
                foreach (var (id, _) in paid)
                {
                    var p = items.FirstOrDefault(i => i.InvoiceId == id);
                    if (p == null) continue;
                    var plan = Plans.ByKey(p.Plan);
                    pending.Remove(id);
                    await Delivery.PaidKey(bot, store, keys, cfg, p.ChatId, p.TelegramId, p.Username,
                        $"crypto:{id}", $"{plan.Name}|{plan.Months}", ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { Console.WriteLine($"Crypto poll: {ex.Message}"); }
        }
    }
}
