package com.systemguard.remote

import android.content.Context
import android.graphics.BitmapFactory
import android.util.AttributeSet
import android.view.View
import androidx.appcompat.widget.AppCompatImageView
import kotlinx.coroutines.*

/**
 * MJPEG-плеер для /api/mjpeg и /api/cammjpeg (до 60 FPS).
 * Парсит multipart/x-mixed-replace вручную: читает Content-Length каждого
 * JPEG-кадра и рисует. Токен уже вшит в URL (query), headers не нужны.
 */
class MjpegView @JvmOverloads constructor(
    context: Context, attrs: AttributeSet? = null
) : AppCompatImageView(context, attrs) {

    private var job: Job? = null
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)

    fun play(url: String, token: String) {
        stop()
        job = scope.launch {
            try {
                val conn = java.net.URL(url).openConnection() as java.net.HttpURLConnection
                conn.setRequestProperty("X-Token", token)
                conn.connectTimeout = 12000
                conn.readTimeout = 0
                conn.connect()
                val input = conn.inputStream.buffered()
                while (isActive) {
                    // Ищем границу кадра и Content-Length
                    val frame = readFrame(input) ?: break
                    val bmp = BitmapFactory.decodeByteArray(frame, 0, frame.size) ?: continue
                    withContext(Dispatchers.Main) { setImageBitmap(bmp) }
                }
            } catch (e: Exception) {
                // Тихо останавливаемся; UI показывает последний кадр
            }
        }
    }

    fun stop() {
        job?.cancel()
        job = null
    }

    private fun readFrame(input: java.io.BufferedInputStream): ByteArray? {
        // Читаем header до \r\n\r\n, вытаскиваем Content-Length
        val head = StringBuilder()
        var last4 = IntArray(4) { -1 }
        var i = 0
        while (true) {
            val b = input.read()
            if (b == -1) return null
            head.append(b.toChar())
            last4[i % 4] = b
            i++
            if (i >= 4 && last4[(i - 4) % 4] == 13 && last4[(i - 3) % 4] == 10 &&
                last4[(i - 2) % 4] == 13 && last4[(i - 1) % 4] == 10
            ) break
            if (head.length > 8192) return null
        }
        val m = Regex("Content-Length:\\s*(\\d+)", RegexOption.IGNORE_CASE).find(head.toString())
            ?: return null
        val len = m.groupValues[1].toIntOrNull() ?: return null
        if (len <= 0 || len > 8 * 1024 * 1024) return null
        val buf = ByteArray(len)
        var off = 0
        while (off < len) {
            val n = input.read(buf, off, len - off)
            if (n == -1) return null
            off += n
        }
        return buf
    }

    override fun onDetachedFromWindow() {
        super.onDetachedFromWindow()
        stop()
    }
}
