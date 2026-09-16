# SystemGuard Android — пульт для ПК через Live HTTP API

Нативное Android-приложение (Kotlin): `GET /api/status`, `/api/shot.jpg`,
`/api/mjpeg`, камера, файлы, `POST /api/action`.

Авторизация Live: header `X-Token` **и** `?token=` в URL.

## Окружение (под эту машину)

- Android Studio 2026.1.4 (JBR 21)
- JDK `C:\Program Files\Android\openjdk\jdk-21.0.8`
- SDK `C:\Program Files (x86)\Android\android-sdk`
  (platforms android-35/36, build-tools 36.0.0)
- AGP 8.5.2 + Gradle 8.7 + Kotlin 2.0.20
- `compileSdk 36`, `targetSdk 36`, `minSdk 26`, `buildToolsVersion 36.0.0`

Путь проекта содержит кириллицу (`Рабочий стол`), поэтому в
`gradle.properties` стоит `android.overridePathCheck=true`.
`local.properties` (sdk.dir) уже создан и в git не коммитится.

## Сборка

```bat
.\gradlew.bat :app:assembleDebug
```

Готовый APK: `app/build/outputs/apk/debug/app-debug.apk` (~7 МБ).

Открыть папку в Android Studio → Sync → Run.

## Подключение (только Live)

1. На ПК: SystemGuard → Telegram → **Publish live link**.
2. В приложении: вставить ссылку + токен → **Связать**.
3. Сохраняется в EncryptedSharedPreferences.

## Структура

- `app/.../remote/ApiClient.kt` — Live HTTP API
- `app/.../remote/BotRelayClient.kt` — Relay через Telegram Bot API (запасной)
- `app/.../remote/SecureStore.kt` — зашифрованное хранение
- `app/.../remote/MjpegView.kt` — MJPEG-плеер
- `app/.../remote/MainActivity.kt` — гибрид Live→Relay, вкладки пульта
