# SystemGuard Android — отдельный проект (PARKED)

> Пауза: основной клиент сейчас WebApp. Relay через Bot API из нативного
> приложения невозможен технически (сообщения от имени бота ПК не видит,
> свои ответы бот в getUpdates не получает). Рабочий вариант для APK —
> share-intent с предзаполненной командой чата, это отдельный редизайн.
> До него: только Live. Код BotRelayClient.kt помечен PARKED.

Нативное Android-приложение (Kotlin, Android Studio) — пульт для ПК через
**Live HTTP API**: `GET /api/status`, `/api/shot.jpg`, `/api/mjpeg`,
камера, файлы, `POST /api/action`. Нужен опубликованный live-туннель.

Авторизация Live: на каждый запрос header `X-Token` **и** `?token=` в URL
(чтобы работали и fetch, и `<img>`/плееры без headers).

## Подключение (только Live)

1. На ПК: SystemGuard → Telegram → **Publish live link**.
2. В приложении: вставить ссылку из Publish live link + токен → **Связать**.
3. Сохраняется в EncryptedSharedPreferences. Вне дома — тот же туннель
   (ПК включён, приложение запущено). Свежая ссылка после каждого
   переподключения сама прилетает владельцу в личку Telegram-бота.

Вне дома без live: команды — прямо в чате с ботом
(`/status`, `/cmd`, `/screenshot`…), ПК-бот читает чат из любой сети.

## Сборка

Открыть папку в **Android Studio** (Hedgehog+), дождаться sync Gradle,
Run на телефоне или Build → APK. minSdk 26, targetSdk 34.

Без Android SDK здесь не собирается — это scaffold под Android Studio.

## Структура

- `app/.../remote/ApiClient.kt` — Live HTTP API (быстрый транспорт)
- `app/.../remote/BotRelayClient.kt` — Relay через Telegram Bot API (вне дома)
- `app/.../remote/SecureStore.kt` — зашифрованное хранение ссылки+токена+бота
- `app/.../remote/MjpegView.kt` — MJPEG-плеер (экран/камера до 30 FPS)
- `app/.../remote/MainActivity.kt` — гибрид Live→Relay, вкладки пульта

## Roadmap

1. Milestone 1 (этот scaffold): связь, статус, экран/камера, терминал, питание.
2. Milestone 2: ПК-зеркало (процессы, автозагрузка, сеть, задачи), файлы.
3. Milestone 3: биометрия перед Unlock, виджет «скрин», FCM-wake через 2-е устройство.
