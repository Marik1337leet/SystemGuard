namespace SystemGuard.Desktop.Services;

// ── OAuth / OIDC конфигурация ───────────────────────────────────────────────
// КАК ВКЛЮЧИТЬ НАСТОЯЩИЙ ВХОД:
//  1. Google: https://console.cloud.google.com → APIs & Services → Credentials →
//     Create OAuth client ID → тип "Desktop app". Вставь Client ID ниже.
//     Redirect URI loopback (http://127.0.0.1:*) разрешён для Desktop-типа по умолчанию.
//  2. Apple: https://developer.apple.com (нужен платный Developer Program) →
//     Identifiers → Services ID (нужен Return URL вида https://твой-домен/auth,
//     для loopback DEV-режима оставь заглушку) → Keys → Sign in with Apple key
//     (Team ID, Key ID, .p8). Вставь все четыре значения ниже.
// Без ключей кнопки входа покажут точную инструкцию, что вставить.
public static class AuthConfig
{
    public const string GoogleClientId = "PASTE_GOOGLE_DESKTOP_CLIENT_ID_HERE";
    public const string GoogleClientSecret = ""; // для Desktop-типа обычно пусто (PKCE не используется, loopback)

    public const string AppleServiceId = "PASTE_APPLE_SERVICE_ID_HERE"; // напр. com.systemguard.signin
    public const string AppleTeamId = "PASTE_APPLE_TEAM_ID";
    public const string AppleKeyId = "PASTE_APPLE_KEY_ID";
    public const string ApplePrivateKeyP8 = "PASTE_APPLE_P8_PRIVATE_KEY"; // содержимое .p8 целиком

    public static bool IsGoogleConfigured =>
        !GoogleClientId.StartsWith("PASTE_");

    public static bool IsAppleConfigured =>
        !AppleServiceId.StartsWith("PASTE_") &&
        !AppleTeamId.StartsWith("PASTE_") &&
        !AppleKeyId.StartsWith("PASTE_") &&
        !ApplePrivateKeyP8.StartsWith("PASTE_");
}
