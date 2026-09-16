# SystemGuard ShopBot — бот-магазин лицензий

Отдельный бот для продажи Pro. Запуск только на сервере / твоём ПК.
НЕ включать в дистрибутив Desktop-приложения.

## 1. Что мне дать (аккаунты и ключи)

1. **Токен шоп-бота** — уже есть, НО он засвечен в переписке.
   → BotFather → твой бот → **Revoke current token**, новый токен положишь сам в конфиг (п.2).
   В код токен не вшивается никогда.
2. **Supabase** (ты выбрал «сразу Supabase»):
   - supabase.com → New project → скопируй **Project URL** и **anon public key** + **service_role key**
   - SQL Editor → выполнить `supabase_schema.sql` из этой папки
   - Куда вписать:
     - Desktop (чтение своих покупок): `%LocalAppData%\SystemGuard\supabase.json`
       `{ "supabaseUrl": "...", "anonKey": "eyJ..." }` (только anon!)
     - Shop-bot (запись продаж): `%LocalAppData%\SystemGuard\shopbot.json`
       `{ "botToken": "...", "ownerId": 123, "supabaseUrl": "...",
          "supabaseServiceKey": "...", "cryptoPayToken": "..." }`
     - Пример: `shopbot.example.json`. service key — только серверу.
3. **Криптоплатежка** — @CryptoBot → Crypto Pay → Create App → **API token** → в `cryptoPayToken`.
   Уже вписан и проверен (приложение `System Guard`, счета создаются/удаляются).
   Цены USDT меняются в одном месте: таблица `Plans.All` в `Program.cs`
   (сейчас Monthly 4 / Half-Year 12 / Yearly 20 / Lifetime 35).
   Как работает: бот создаёт счёт на 1 час → фоновая проверка каждые 30 сек →
   оплачен → ключ сам прилетает покупателю + запись в БД. Протух — бот пишет
   «выставлю новый». Неоплаченные счета переживают рестарт (файл `crypto_pending.json`).
4. **Username шоп-бота** — `@SystemGuardPayBot`, уже вписан в
   `LicenseViewModel.ShopBotUsername`. Токен туда НЕ писать.

## 2. Запуск

```
set SHOP_BOT_TOKEN=...   (или shopbot.json)
dotnet run --project SystemGuard.ShopBot
```

Без Supabase работает файловый режим (`shop/sales.jsonl`) — продажи не потеряются,
облако подключишь позже без переписывания кода (интерфейс `IShopStore`).

## 3. Как продаются лицензии (схема)

- **В приложении**: `Settings → License → Buy Pro in Telegram` открывает
  `t.me/<shopbot>?start=buy_<HWID>`. Бот запоминает HWID и привязывает ключ к этому ПК.
  После оплаты: кнопка **«I paid — check»** находит покупку по HWID и активирует Pro
  **без показа ключа** (полный текст ключа в облаке не хранится — только `key_prefix`).
  Ключ из чата подхватывается из буфера обмена и сразу стирается.
- **Через бота**: оплата Stars/криптой → бот присылает ключ спойлером + чек тебе в личку
  (тариф, сумма, от кого, HWID, ключ). Пользователь жмёт «I paid — check» в приложении.
- **Розыгрыши**: ключи генерируешь KeyGenerator'ом, раздаёшь сам;
  вводятся в `Settings → License → «I have a gift key (giveaway)»` (поле спрятано,
  обычный покупатель его не видит).

## 4. Управление пользователями (без разницы: сайт или приложение — всё через Supabase)

- Таблица `licenses`: продлить — `expires_at`, забанить — `revoked = true`,
  мульти-ПК (Enterprise) — несколько строк с разными `hwid` на один `telegram_id`.
- Таблица `customers` — кто есть кто. `payments` — аудит оплат.
- Позже к тем же таблицам подключается сайт/админка — схема уже готова.
- Stars выводятся: BotFather → My Bots → Payments → Fragment (чек дублируется тебе в личку).

## 5. Безопасность релиза (что уже сделано / что осталось тебе)

- Сделано: токен нигде в коде; генерация ключей только в shop-bot (в Desktop-клиенте
  остался только валидатор — это нормально); WebSocket-инъекция `& | ;` заблокирована;
  `/api`-probe отдаёт только `{ok, service}` без версии; триал одноразовый.
- Сделай: отзови засвеченный токен (п.1); никому не показывай service key;
  `license.dat`/`telegram.json`/`remote.json` лежат открытым текстом в
  `%LocalAppData%\SystemGuard` — следующий шаг: DPAPI-шифрование (запланировано).
