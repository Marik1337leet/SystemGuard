package com.systemguard.remote

import android.app.DownloadManager
import android.content.Context
import android.graphics.BitmapFactory
import android.net.Uri
import android.os.Bundle
import android.os.Environment
import android.widget.Toast
import androidx.appcompat.app.AppCompatActivity
import androidx.lifecycle.lifecycleScope
import com.systemguard.remote.databinding.ActivityMainBinding
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import okhttp3.OkHttpClient
import okhttp3.Request
import java.util.concurrent.TimeUnit

/**
 * Milestone 1: связь, статус, питание, экран/камера (кадр + видео),
 * звук/медиа, процессы, файлы, терминал. Всё — через ApiClient (Live API).
 */
class MainActivity : AppCompatActivity() {

    private lateinit var b: ActivityMainBinding
    private lateinit var store: SecureStore
    private lateinit var api: ApiClient
    private lateinit var relay: BotRelayClient

    private val imgHttp = OkHttpClient.Builder()
        .connectTimeout(12, TimeUnit.SECONDS)
        .readTimeout(20, TimeUnit.SECONDS)
        .build()

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        b = ActivityMainBinding.inflate(layoutInflater)
        setContentView(b.root)
        store = SecureStore(this)
        api = ApiClient(store)
        relay = BotRelayClient(store)

        b.inUrl.setText(store.baseUrl)
        b.inToken.setText(store.token)
        b.inBotToken.setText(store.botToken)
        b.inBotChat.setText(store.botChatId)
        refreshBadge()
        refreshRelayState()

        b.btnLink.setOnClickListener {
            val u = b.inUrl.text.toString().trim().trimEnd('/')
            val t = b.inToken.text.toString().trim()
            if (!u.startsWith("https://")) {
                toast("Нужна https-ссылка из Publish live link"); return@setOnClickListener
            }
            if (t.isEmpty()) { toast("Введите токен"); return@setOnClickListener }
            store.baseUrl = u; store.token = t
            b.txtState.text = "Проверка связи…"
            lifecycleScope.launch {
                when (val r = api.status()) {
                    is ApiClient.Result.Ok -> {
                        b.txtState.text = "Связано"
                        toast("Live связано"); refreshBadge(); showStatus()
                    }
                    is ApiClient.Result.Err -> {
                        b.txtState.text = "Нет связи"
                        toast(r.error)
                    }
                }
            }
        }
        b.btnUnlink.setOnClickListener {
            b.screenView.stop(); b.camView.stop()
            store.clearLive()
            b.inUrl.setText(""); b.inToken.setText("")
            b.txtState.text = "Не связано"; refreshBadge()
        }
        // ── Relay: тот же бот что на ПК, работает вне дома без туннеля ──
        b.btnRelayLink.setOnClickListener {
            val t = b.inBotToken.text.toString().trim()
            val c = b.inBotChat.text.toString().trim()
            if (t.length < 20) { toast("Вставьте токен бота (тот же, что на ПК)"); return@setOnClickListener }
            if (c.toLongOrNull() == null) { toast("Chat ID — цифры (узнать: @userinfobot)"); return@setOnClickListener }
            store.botToken = t; store.botChatId = c
            b.txtRelayState.text = "Relay: проверка…"
            lifecycleScope.launch {
                when (val r = relay.checkLink()) {
                    is BotRelayClient.RelayResult.Ok -> {
                        b.txtRelayState.text = "Relay: связано (вне дома работает)"
                        toast("Relay связано — команды идут через Telegram"); refreshBadge()
                    }
                    is BotRelayClient.RelayResult.Err -> {
                        b.txtRelayState.text = "Relay: нет связи"
                        toast(r.error)
                    }
                }
            }
        }

        b.btnPerf.setOnClickListener { cmd("perf", b.txtStatus) }
        b.btnSysinfo.setOnClickListener { cmd("sysinfo", b.txtStatus) }
        b.btnShutdown.setOnClickListener { cmd("shutdown", b.txtPower) }
        b.btnRestart.setOnClickListener { cmd("restart", b.txtPower) }
        b.btnSleep.setOnClickListener { cmd("sleep", b.txtPower) }
        b.btnLock.setOnClickListener { cmd("lock", b.txtPower) }
        b.btnWol.setOnClickListener {
            val m = b.inWol.text.toString().trim()
            if (m.isEmpty()) { toast("Введите MAC"); return@setOnClickListener }
            cmd("wol", b.txtPower, m)
        }
        b.btnUnlock.setOnClickListener {
            val p = b.inPass.text.toString()
            if (p.isEmpty()) { toast("Введите пароль"); return@setOnClickListener }
            b.inPass.setText("")
            cmd("unlock", b.txtPower, p)
        }

        b.btnShot.setOnClickListener {
            // Вне дома Live мёртв — скриншот прилетит фоткой в чат с ботом.
            if (!store.isLinked && store.isRelayLinked) {
                toast("Live нет — скриншот идёт в чат с ботом")
                cmd("screenshot", b.txtStatus); return@setOnClickListener
            }
            loadImage(api.screenShotUrl(), b.screenView)
        }
        b.btnVideo.setOnClickListener {
            if (!store.isLinked) {
                toast(if (store.isRelayLinked) "Видео только по Live; команды — через Relay. Опубликуйте live на ПК" else "Сначала свяжите ПК")
                return@setOnClickListener
            }
            b.screenView.play(api.screenStreamUrl(), store.token)
            toast("Видео экрана: ${store.fps} FPS")
        }
        b.btnVideoStop.setOnClickListener {
            b.screenView.stop(); loadImage(api.screenShotUrl(), b.screenView)
        }
        b.btnCam.setOnClickListener {
            if (!store.isLinked && store.isRelayLinked) {
                toast("Live нет — фото с камеры идёт в чат с ботом")
                cmd("cam", b.txtStatus); return@setOnClickListener
            }
            loadImage(api.camShotUrl(), b.camView)
        }
        b.btnCamVideo.setOnClickListener {
            if (!store.isLinked) {
                toast(if (store.isRelayLinked) "Видео только по Live; фото — кнопкой Фото (в чат)" else "Сначала свяжите ПК")
                return@setOnClickListener
            }
            b.camView.play(api.camStreamUrl(), store.token)
            toast("Видео камеры: ${store.fps} FPS")
        }
        b.btnCamStop.setOnClickListener { b.camView.stop() }

        b.btnVolDown.setOnClickListener { cmd("volume", null, "down") }
        b.btnVolUp.setOnClickListener { cmd("volume", null, "up") }
        b.btnMute.setOnClickListener { cmd("mute", null) }
        b.btnPrev.setOnClickListener { cmd("prev", null) }
        b.btnPlay.setOnClickListener { cmd("play", null) }
        b.btnNext.setOnClickListener { cmd("next", null) }

        b.btnProcs.setOnClickListener { cmd("processes", b.txtPc) }
        b.btnNet.setOnClickListener { cmd("connections", b.txtPc) }
        b.btnStartup.setOnClickListener { cmd("startup", b.txtPc) }
        b.btnKill.setOnClickListener {
            val n = b.inKill.text.toString().trim()
            if (n.isEmpty()) { toast("Введите имя процесса"); return@setOnClickListener }
            cmd("close", b.txtPc, n)
        }
        b.btnLs.setOnClickListener {
            if (!store.isAnyLinked) { toast("Сначала свяжите ПК: Live или Relay"); return@setOnClickListener }
            lifecycleScope.launch {
                val path = b.inLs.text.toString().trim()
                if (store.isLinked) {
                    when (val r = api.run("ls", path)) {
                        is ApiClient.Result.Ok -> {
                            val items = r.json.optJSONArray("items")
                            val sb = StringBuilder(r.json.optString("path", "") + "\n")
                            if (items != null) for (i in 0 until items.length()) {
                                val o = items.getJSONObject(i)
                                sb.append(if (o.optString("type") == "dir") "[D] " else "[F] ")
                                    .append(o.optString("name")).append('\n')
                            }
                            b.txtFiles.text = sb.toString()
                            return@launch
                        }
                        is ApiClient.Result.Err -> {
                            if (!store.isRelayLinked) { toast(r.error); return@launch }
                        }
                    }
                }
                when (val r = relay.run("ls", path)) {
                    is BotRelayClient.RelayResult.Ok -> b.txtFiles.text = "[Relay] " + r.output.take(4000)
                    is BotRelayClient.RelayResult.Err -> toast(r.error)
                }
            }
        }
        b.btnCmd.setOnClickListener {
            val c = b.inCmd.text.toString().trim()
            if (c.isEmpty()) { toast("Введите команду"); return@setOnClickListener }
            cmd("cmd", b.txtCmd, c)
        }
    }

    private fun refreshBadge() {
        b.connBadge.text = when {
            store.isLinked && store.isRelayLinked -> "● Live + Relay"
            store.isLinked -> "● Live"
            store.isRelayLinked -> "● Relay (вне дома)"
            else -> "○ Офлайн"
        }
    }

    private fun refreshRelayState() {
        b.txtRelayState.text =
            if (store.isRelayLinked) "Relay: связано (вне дома работает)" else "Relay: не связан"
    }

    private fun showStatus() {
        lifecycleScope.launch {
            // Вне дома Live мёртв — статус тянем через Relay.
            if (store.isLinked) {
                when (val r = api.status()) {
                    is ApiClient.Result.Ok -> {
                        val j = r.json
                        b.txtStatus.text =
                            "${j.optString("machine")} · ${j.optString("time")}\n" +
                            "RAM ${j.optDouble("ramUsedGb")}/${j.optDouble("ramTotalGb")} GB\n" +
                            "${j.optString("battery")} · uptime ${j.optString("uptime")}"
                        return@launch
                    }
                    is ApiClient.Result.Err -> {
                        // Live упал — молча пробуем Relay ниже, а не показываем ошибку.
                        if (!store.isRelayLinked) { b.txtStatus.text = r.error; return@launch }
                    }
                }
            }
            when (val r = relay.run("status")) {
                is BotRelayClient.RelayResult.Ok -> b.txtStatus.text = "[Relay] " + r.output.take(2000)
                is BotRelayClient.RelayResult.Err -> b.txtStatus.text = r.error
            }
        }
    }

    /**
     * Гибрид: быстрый Live первым, неубиваемый Relay запасным.
     * Именно это чинит "вне дома ничего не работает": раньше при мёртвом
     * туннеле команды просто умирали, теперь идут через Telegram.
     */
    private fun cmd(action: String, view: android.widget.TextView?, arg: String = "") {
        if (!store.isAnyLinked) {
            toast("Сначала свяжите ПК: Live или Relay"); return
        }
        lifecycleScope.launch {
            if (view != null) view.text = "Выполняю ($action)…"
            // 1) Live — мгновенно, когда туннель жив.
            if (store.isLinked) {
                when (val r = api.run(action, arg)) {
                    is ApiClient.Result.Ok -> {
                        val t = r.output.ifEmpty { r.message }
                        if (view != null) view.text = t.take(4000)
                        else if (t.isNotEmpty()) toast(t.take(120))
                        return@launch
                    }
                    is ApiClient.Result.Err -> {
                        // 401 = токен протух: дальше Relay бессмысленно молчать — скажем прямо.
                        if (r.error.contains("401") && !store.isRelayLinked) {
                            if (view != null) view.text = r.error else toast(r.error)
                            return@launch
                        }
                        // Иначе — тихо падаем на Relay (туннель сменил URL/упал).
                    }
                }
            }
            // 2) Relay — через Telegram из любой сети (1-3с).
            if (!store.isRelayLinked) {
                val t = "Live недоступен, а Relay не связан — введите токен бота и Chat ID"
                if (view != null) view.text = t else toast(t)
                return@launch
            }
            if (view != null) view.text = "Live недоступен, иду через Relay…"
            when (val r = relay.run(action, arg)) {
                is BotRelayClient.RelayResult.Ok -> {
                    val t = r.output.ifEmpty { r.message }
                    if (view != null) view.text = "[Relay] " + t.take(4000)
                    else if (t.isNotEmpty()) toast(("[Relay] " + t).take(150))
                }
                is BotRelayClient.RelayResult.Err -> {
                    if (view != null) view.text = r.error else toast(r.error)
                }
            }
        }
    }

    private fun loadImage(url: String, view: MjpegView) {
        // Кадры — только по Live (HTTP). Вне дома без туннеля скриншоты
        // прилетают фоткой в чат с ботом (кнопки Кадр/Фото сами уходят в Relay).
        if (!store.isLinked) {
            if (store.isRelayLinked) toast("Live нет — кадр уже запрошен в чат с ботом")
            else toast("Сначала свяжите ПК")
            return
        }
        lifecycleScope.launch {
            try {
                val bytes = withContext(Dispatchers.IO) {
                    val req = Request.Builder().url(url)
                        .header("X-Token", store.token).build()
                    imgHttp.newCall(req).execute().use { r ->
                        if (!r.isSuccessful) throw Exception("HTTP ${r.code}")
                        r.body?.bytes() ?: throw Exception("Пустой кадр")
                    }
                }
                val bmp = BitmapFactory.decodeByteArray(bytes, 0, bytes.size)
                if (bmp != null) view.setImageBitmap(bmp) else toast("Не decode кадра")
            } catch (e: Exception) {
                toast("Кадр: ${e.message}")
            }
        }
    }

    /** Скачивание с ПК прямой ссылкой туннеля (до 500 МБ) — показать в Milestone 2. */
    @Suppress("unused")
    private fun downloadFromPc(pcPath: String) {
        if (!store.isLinked) {
            toast(if (store.isRelayLinked) "Файлы качаются только по Live" else "Сначала свяжите ПК")
            return
        }
        val url = api.mediaUrl("/api/file?path=" + Uri.encode(pcPath))
        val req = DownloadManager.Request(Uri.parse(url))
            .addRequestHeader("X-Token", store.token)
            .setTitle(pcPath.substringAfterLast('\\'))
            .setDestinationInExternalPublicDir(
                Environment.DIRECTORY_DOWNLOADS,
                "SG_" + pcPath.substringAfterLast('\\')
            )
            .setNotificationVisibility(DownloadManager.Request.VISIBILITY_VISIBLE_NOTIFY_COMPLETED)
        (getSystemService(Context.DOWNLOAD_SERVICE) as DownloadManager).enqueue(req)
        toast("Скачивание началось")
    }

    private fun toast(s: String) =
        Toast.makeText(this, s, Toast.LENGTH_SHORT).show()

    override fun onDestroy() {
        b.screenView.stop(); b.camView.stop()
        super.onDestroy()
    }
}
