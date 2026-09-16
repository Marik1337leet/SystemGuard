package com.systemguard.remote

import android.content.Context
import androidx.security.crypto.EncryptedSharedPreferences
import androidx.security.crypto.MasterKey

/** Зашифрованное хранение live-ссылки и токена (ничего в открытом виде). */
class SecureStore(context: Context) {
    private val prefs = EncryptedSharedPreferences.create(
        context,
        "sg_remote",
        MasterKey.Builder(context).setKeyScheme(MasterKey.KeyScheme.AES256_GCM).build(),
        EncryptedSharedPreferences.PrefKeyEncryptionScheme.AES256_SIV,
        EncryptedSharedPreferences.PrefValueEncryptionScheme.AES256_GCM
    )

    var baseUrl: String
        get() = prefs.getString("base_url", "") ?: ""
        set(v) = prefs.edit().putString("base_url", v.trim().trimEnd('/')).apply()

    var token: String
        get() = prefs.getString("token", "") ?: ""
        set(v) = prefs.edit().putString("token", v.trim()).apply()

    var fps: Int
        get() = prefs.getInt("fps", 15)
        set(v) = prefs.edit().putInt("fps", v.coerceIn(1, 30)).apply()

    var quality: Int
        get() = prefs.getInt("quality", 55)
        set(v) = prefs.edit().putInt("quality", v.coerceIn(30, 85)).apply()

    // ── Bot Relay (вне дома без туннеля) ─────────────────────────────
    // Тот же бот, что слушает ПК. Телефон шлёт команды обычными сообщениями
    // через Bot API, ПК отвечает в тот же чат. Работает из любой сети.
    var botToken: String
        get() = prefs.getString("bot_token", "") ?: ""
        set(v) = prefs.edit().putString("bot_token", v.trim()).apply()

    var botChatId: String
        get() = prefs.getString("bot_chat_id", "") ?: ""
        set(v) = prefs.edit().putString("bot_chat_id", v.trim()).apply()

    val isRelayLinked: Boolean get() = botToken.length >= 20 && botChatId.toLongOrNull() != null

    val isLinked: Boolean get() = baseUrl.startsWith("https://") && token.isNotEmpty()

    /** Хоть один транспорт готов: быстрый Live или неубиваемый Relay. */
    val isAnyLinked: Boolean get() = isLinked || isRelayLinked

    /** Сброс только Live (кнопка "Отключить" не должна убивать Relay). */
    fun clearLive() {
        baseUrl = ""
        token = ""
    }

    fun clear() = prefs.edit().clear().apply()
}
