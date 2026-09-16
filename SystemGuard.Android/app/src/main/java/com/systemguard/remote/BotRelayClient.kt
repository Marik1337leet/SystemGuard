package com.systemguard.remote

import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.withContext
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import org.json.JSONObject
import java.net.URLEncoder
import java.util.UUID
import java.util.concurrent.TimeUnit

/**
 * PARKED (не использовать): relay через Bot API из нативного приложения
 * невозможен — сообщение, отправленное с токеном бота, исходит ОТ бота,
 * а ПК-бот такие не видит (getUpdates отдаёт только сообщения пользователей);
 * ответы бота в getUpdates тоже не приходят. Рабочая схема для APK —
 * share-intent с предзаполненной командой чата (пользователь отправляет
 * сам). До редизайна: только Live.
 *
 * Неубиваемый транспорт "вне дома": команды идут через Telegram Bot API
 * обычными сообщениями, ПК-бот исполняет и отвечает в тот же чат.
 * Не нужен туннель, белый IP, проброс портов. Задержка 1-3с.
 *
 * Протокол (зеркало BotRelayProtocol.cs на ПК):
 *   → 🤖SG:{"id":"...","action":"status","arg":""}
 *   ← 🤖SG-RESP:{"id":"...","ok":true,"message":"...","output":"..."}
 */
class BotRelayClient(private val store: SecureStore) {

    private val http = OkHttpClient.Builder()
        .connectTimeout(15, TimeUnit.SECONDS)
        .readTimeout(20, TimeUnit.SECONDS)
        .writeTimeout(20, TimeUnit.SECONDS)
        .build()

    companion object {
        const val REQ_PREFIX = "🤖SG:"
        const val RESP_PREFIX = "🤖SG-RESP:"
    }

    sealed interface RelayResult {
        data class Ok(val message: String, val output: String, val json: JSONObject) : RelayResult
        data class Err(val error: String) : RelayResult
    }

    val isReady: Boolean get() = store.isRelayLinked

    private fun api(method: String): String =
        "https://api.telegram.org/bot${store.botToken}/$method"

    private fun enc(s: String): String = URLEncoder.encode(s, "UTF-8")

    /**
     * Отправить команду и дождаться ответа ПК (long-poll getUpdates).
     * timeoutSec — сколько ждём ПК (он отвечает за ~1-3с, 45с с запасом
     * на холодный polling-цикл бота 20с + сеть).
     */
    suspend fun run(action: String, arg: String = "", timeoutSec: Int = 45): RelayResult =
        withContext(Dispatchers.IO) {
            if (!isReady) return@withContext RelayResult.Err("Relay не связан: введите токен бота и Chat ID")
            val id = UUID.randomUUID().toString().replace("-", "")
            val packet = REQ_PREFIX + JSONObject()
                .put("id", id)
                .put("action", action.trim())
                .put("arg", arg)
                .toString()
            try {
                // 0) Узнаём текущий offset, чтобы не подбирать чужие старые ответы.
                val startOffset = lastUpdateId() + 1
                // 1) Шлём команду как обычное сообщение себе в чат (боту).
                val sendOk = sendMessage(store.botChatId, packet)
                if (sendOk == null) return@withContext RelayResult.Err("Bot API не ответил (токен/интернет?)")
                // 2) Ждём SG-RESP с нашим id.
                val deadline = System.currentTimeMillis() + timeoutSec * 1000L
                var offset = startOffset
                while (System.currentTimeMillis() < deadline) {
                    val updates = getUpdates(offset, 10)
                    if (updates != null) {
                        for (i in 0 until updates.length()) {
                            val u = updates.getJSONObject(i)
                            offset = u.optLong("update_id", offset) + 1
                            // Ответ ПК — текстовое SG-RESP; фото скриншотов живут
                            // в чате рядом (caption [relay:id]) и не мешают.
                            val t1 = u.optJSONObject("message")?.optString("text", "") ?: ""
                            val t2 = u.optJSONObject("edited_message")?.optString("text", "") ?: ""
                            val text = if (t1.startsWith(RESP_PREFIX)) t1
                                else if (t2.startsWith(RESP_PREFIX)) t2 else continue
                            try {
                                val body = JSONObject(text.removePrefix(RESP_PREFIX))
                                if (body.optString("id") != id) continue
                                val ok = body.optBoolean("ok", true)
                                val msg = body.optString("message", "")
                                val out = body.optString("output", msg)
                                return@withContext if (ok)
                                    RelayResult.Ok(msg.ifEmpty { "OK" }, out, body)
                                else
                                    RelayResult.Err(msg.ifEmpty { "Ошибка ПК" })
                            } catch (_: Exception) { /* чужой/битый пакет — дальше */ }
                        }
                    }
                    delay(1500)
                }
                RelayResult.Err("ПК не ответил за ${timeoutSec}с (выключен, нет интернета или бот не запущен)")
            } catch (e: Exception) {
                RelayResult.Err(humanize(e))
            }
        }

    /** Быстрая проверка: токен живой и чат доступен (без ожидания ПК). */
    suspend fun checkLink(): RelayResult = withContext(Dispatchers.IO) {
        if (!isReady) return@withContext RelayResult.Err("Введите токен бота и Chat ID")
        try {
            val req = Request.Builder().url(api("getMe")).build()
            http.newCall(req).execute().use { r ->
                val body = r.body?.string() ?: ""
                if (!r.isSuccessful || !body.contains("\"ok\":true"))
                    return@withContext RelayResult.Err("Токен бота неверный (Bot API отказал)")
            }
            RelayResult.Ok("Бот найден", "", JSONObject())
        } catch (e: Exception) {
            RelayResult.Err(humanize(e))
        }
    }

    // ── Bot API primitives ───────────────────────────────────────────

    private fun lastUpdateId(): Long {
        return try {
            val req = Request.Builder()
                .url(api("getUpdates") + "?limit=1&timeout=0&allowed_updates=" + enc("[\"message\"]"))
                .build()
            http.newCall(req).execute().use { r ->
                val body = r.body?.string() ?: return 0
                val arr = JSONObject(body).optJSONArray("result") ?: return 0
                if (arr.length() == 0) return 0
                arr.getJSONObject(0).optLong("update_id", 0)
            }
        } catch (_: Exception) { 0 }
    }

    private fun sendMessage(chatId: String, text: String): Boolean? {
        return try {
            val json = JSONObject().put("chat_id", chatId).put("text", text).toString()
                .toRequestBody("application/json".toMediaType())
            val req = Request.Builder().url(api("sendMessage")).post(json).build()
            http.newCall(req).execute().use { r ->
                if (!r.isSuccessful) return null
                r.body?.string()?.contains("\"ok\":true") == true
            }
        } catch (_: Exception) { null }
    }

    private fun getUpdates(offset: Long, timeout: Int): org.json.JSONArray? {
        return try {
            val url = api("getUpdates") + "?offset=$offset&limit=50&timeout=$timeout" +
                "&allowed_updates=" + enc("[\"message\",\"edited_message\"]")
            val req = Request.Builder().url(url).build()
            http.newCall(req).execute().use { r ->
                if (!r.isSuccessful) return null
                JSONObject(r.body?.string() ?: "").optJSONArray("result")
            }
        } catch (_: Exception) { null }
    }

    private fun humanize(e: Exception): String {
        val m = e.message ?: ""
        return when {
            m.contains("timeout", true) -> "Таймаут сети: проверьте интернет на телефоне"
            m.contains("Unable to resolve host", true) -> "Нет интернета на телефоне"
            m.contains("401", true) -> "Токен бота неверный (401)"
            m.contains("chat not found", true) -> "Chat ID неверный или боту не писали /start"
            else -> "Relay: $m"
        }
    }
}
