package com.systemguard.remote

import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import org.json.JSONObject
import java.net.URLEncoder
import java.util.concurrent.TimeUnit

/**
 * Тот же Live HTTP API, что использует WebApp v4.
 * Токен шлём и header'ом, и query — сервер принимает оба.
 */
class ApiClient(private val store: SecureStore) {

    private val http = OkHttpClient.Builder()
        .connectTimeout(12, TimeUnit.SECONDS)
        .readTimeout(20, TimeUnit.SECONDS)
        .writeTimeout(20, TimeUnit.SECONDS)
        .build()

    sealed interface Result {
        data class Ok(val message: String, val output: String, val json: JSONObject) : Result
        data class Err(val error: String) : Result
    }

    private fun base(): String = store.baseUrl.trim().trimEnd('/')
    private fun enc(s: String): String = URLEncoder.encode(s, "UTF-8")

    /** URL для <img>/плееров без headers: токен только в query. */
    fun mediaUrl(path: String): String {
        val sep = if (path.contains("?")) "&" else "?"
        return base() + path + sep + "token=" + enc(store.token)
    }

    fun screenShotUrl(w: Int = 960): String =
        mediaUrl("/api/shot.jpg?w=$w&q=${store.quality}&t=${System.currentTimeMillis()}")

    fun screenStreamUrl(fps: Int = store.fps): String =
        mediaUrl("/api/mjpeg?fps=$fps&q=${store.quality}&w=960")

    fun camShotUrl(): String =
        mediaUrl("/api/cam.jpg?t=${System.currentTimeMillis()}")

    fun camStreamUrl(fps: Int = store.fps): String =
        mediaUrl("/api/cammjpeg?fps=$fps&q=${store.quality}")

    suspend fun status(): Result = get("/api/status")

    /** Все команды зеркала: volume, brightness, cmd, ls, processes, startup, ... */
    suspend fun run(action: String, arg: String = ""): Result {
        val body = JSONObject().put("action", action).put("arg", arg)
            .toString().toRequestBody("application/json".toMediaType())
        return withContext(Dispatchers.IO) {
            try {
                val req = Request.Builder()
                    .url(base() + "/api/action?token=" + enc(store.token))
                    .header("X-Token", store.token)
                    .post(body)
                    .build()
                http.newCall(req).execute().use { r ->
                    if (r.code == 401) return@withContext Result.Err("Неверный токен (401)")
                    if (!r.isSuccessful) return@withContext Result.Err("HTTP ${r.code}")
                    parse(r.body?.string() ?: "")
                }
            } catch (e: Exception) {
                Result.Err(humanize(e))
            }
        }
    }

    suspend fun policy(kind: String): Result = get("/api/policy?kind=$kind")

    private suspend fun get(path: String): Result = withContext(Dispatchers.IO) {
        try {
            val sep = if (path.contains("?")) "&" else "?"
            val req = Request.Builder()
                .url(base() + path + sep + "token=" + enc(store.token))
                .header("X-Token", store.token)
                .build()
            http.newCall(req).execute().use { r ->
                if (r.code == 401) return@withContext Result.Err("Неверный токен (401)")
                if (!r.isSuccessful) return@withContext Result.Err("HTTP ${r.code}")
                parse(r.body?.string() ?: "")
            }
        } catch (e: Exception) {
            Result.Err(humanize(e))
        }
    }

    private fun parse(raw: String): Result {
        return try {
            val j = JSONObject(raw)
            if (j.optBoolean("ok", true)) {
                Result.Ok(
                    j.optString("message", ""),
                    j.optString("output", j.optString("message", "")),
                    j
                )
            } else {
                Result.Err(j.optString("error", "Ошибка"))
            }
        } catch (e: Exception) {
            Result.Err("Плохой ответ сервера")
        }
    }

    private fun humanize(e: Exception): String {
        val m = e.message ?: ""
        return when {
            m.contains("timeout", true) || e is java.net.SocketTimeoutException ->
                "Таймаут: ПК не ответил (спит, выключен или туннель упал)"
            m.contains("Unable to resolve host", true) || m.contains("Failed to connect", true) ->
                "Нет связи: проверьте ссылку туннеля (он меняет URL при перезапуске) и интернет"
            m.contains("SSL", true) || m.contains("Chain validation", true) ->
                "TLS-ошибка: ссылка должна начинаться с https://"
            else -> "Нет связи: $m"
        }
    }
}
