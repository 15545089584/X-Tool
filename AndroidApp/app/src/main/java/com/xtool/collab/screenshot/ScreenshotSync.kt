package com.xtool.collab.screenshot

import android.content.ContentUris
import android.content.Context
import android.database.ContentObserver
import android.net.Uri
import android.os.Build
import android.os.Handler
import android.os.Looper
import android.provider.MediaStore
import com.xtool.collab.data.SessionStore
import com.xtool.collab.data.TransferRuntime
import kotlinx.coroutines.*
import kotlinx.coroutines.channels.Channel
import okhttp3.*
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.HttpUrl.Companion.toHttpUrl
import okio.BufferedSink
import java.io.IOException
import java.util.concurrent.TimeUnit
import kotlin.coroutines.resume

/** 观察媒体库中已完成的新截图与相机照片；不截取屏幕、不回扫历史图片。 */
class ScreenshotSync(private val context: Context, scope: CoroutineScope) : AutoCloseable {
    private val session = SessionStore(context)
    private val wake = Channel<Unit>(Channel.CONFLATED)
    private val collection = MediaStore.Images.Media.EXTERNAL_CONTENT_URI
    private val client = OkHttpClient.Builder().connectTimeout(5, TimeUnit.SECONDS)
        .callTimeout(30, TimeUnit.SECONDS).retryOnConnectionFailure(false).build()
    private var generation = -1L
    private var baseline = Long.MAX_VALUE
    private val completed = LinkedHashSet<Long>()
    private val retryAfter = mutableMapOf<Long, Long>()
    @Volatile private var fastScanUntil = 0L
    private val settingsListener = android.content.SharedPreferences.OnSharedPreferenceChangeListener { _, _ ->
        wake.trySend(Unit)
    }
    private val observer = object : ContentObserver(Handler(Looper.getMainLooper())) {
        override fun onChange(selfChange: Boolean) {
            fastScanUntil = android.os.SystemClock.elapsedRealtime() + 2500
            wake.trySend(Unit)
        }
    }
    private val job: Job
    private val connectionJob: Job
    init {
        runCatching { context.contentResolver.registerContentObserver(collection, true, observer) }
        ScreenshotSyncSettings.prefs(context).registerOnSharedPreferenceChangeListener(settingsListener)
        connectionJob = scope.launch {
            TransferRuntime.connected.collect { wake.trySend(Unit) }
        }
        job = scope.launch(Dispatchers.IO) {
            while (isActive) {
                try { scan() }
                catch (e: CancellationException) { throw e }
                catch (_: SecurityException) { ScreenshotSyncSettings.status.value = "需要重新授予图片读取权限" }
                catch (_: Exception) { ScreenshotSyncSettings.status.value = "暂未同步，连接恢复后会重试近期图片" }
                // 已写完的图片不再固定等待 600ms；写入阶段短时补查，闲时降低频率。
                val waitMillis = when {
                    !permitted() || !TransferRuntime.connected.value -> 5000L
                    android.os.SystemClock.elapsedRealtime() < fastScanUntil -> 150L
                    else -> 1000L
                }
                withTimeoutOrNull(waitMillis) {
                    wake.receive()
                    delay(40) // 只合并短时通知风暴，完整性仍由 IS_PENDING 和文件长度检查保证。
                }
            }
        }
    }
    private fun permitted() = (ScreenshotSyncSettings.enabled(context) || ScreenshotSyncSettings.enabled(context, true)) && ScreenshotSyncSettings.allowed(context)
    private fun permitted(camera: Boolean) = ScreenshotSyncSettings.enabled(context, camera) && ScreenshotSyncSettings.allowed(context)
    private suspend fun scan() {
        if (!permitted()) { generation = -1; baseline = Long.MAX_VALUE; completed.clear(); retryAfter.clear(); return }
        val currentGeneration = ScreenshotSyncSettings.prefs(context).getLong("generation", 0)
        if (generation != currentGeneration) {
            // 每次开启或服务恢复都从当前媒体库末尾开始，避免补发旧照片。
            baseline = context.contentResolver.query(collection, arrayOf("_id"), null, null, "_id DESC")?.use {
                if (it.moveToFirst()) it.getLong(0) else 0L
            } ?: 0L
            completed.clear(); retryAfter.clear(); generation = currentGeneration
            ScreenshotSyncSettings.status.value = "已就绪，等待手机新截图或照片"
            return
        }
        if (!TransferRuntime.connected.value || session.token.isBlank()) {
            ScreenshotSyncSettings.status.value = "等待连接电脑；最多重试最近两分钟的新图片"
            return
        }
        val projection = mutableListOf("_id", "_display_name", "_size", "date_added")
        if (Build.VERSION.SDK_INT >= 29) projection.addAll(listOf("relative_path", "is_pending"))
        else projection.add("_data")
        projection.add("datetaken")
        retryAfter.entries.removeAll { it.value < android.os.SystemClock.elapsedRealtime() - 120_000 }
        val rows = mutableListOf<Shot>()
        context.contentResolver.query(collection, projection.toTypedArray(),
            "_id > ? AND date_added >= ?", arrayOf(baseline.toString(), (System.currentTimeMillis()/1000 - 120).toString()), "_id DESC")?.use { c ->
            while (c.moveToNext() && rows.size < 20) {
                val id = c.getLong(0)
                if (id in completed || (retryAfter[id] ?: 0L) > android.os.SystemClock.elapsedRealtime()) continue
                val name = c.getString(1).orEmpty()
                val size = c.getLong(2)
                val path = c.getString(4).orEmpty().replace('\\', '/')
                val pending = Build.VERSION.SDK_INT >= 29 && c.getInt(5) != 0
                val screenshotFolder = path.split('/').any { it.equals("Screenshots", true) || it.equals("Screenshot", true) || it == "截屏" || it == "截图" }
                val directory = if (Build.VERSION.SDK_INT >= 29) path.trimEnd('/') else path.substringBeforeLast('/')
                val cameraFolder = directory.equals("DCIM/Camera", true) || directory.endsWith("/DCIM/Camera", true)
                val takenAt = c.getLong(c.getColumnIndexOrThrow("datetaken"))
                // 相机目录和近期拍摄时间同时满足，避免把导入的旧照片当成刚拍照。
                val camera = !screenshotFolder && cameraFolder && takenAt >= maxOf(currentGeneration, System.currentTimeMillis() - 120_000)
                if ((!screenshotFolder && !camera) || !permitted(camera) || pending || size <= 0 || size > 32L*1024*1024) continue
                if (!name.endsWith(".jpg", true) && !name.endsWith(".jpeg", true) && !name.endsWith(".png", true)) continue
                rows.add(Shot(id, name, size, camera))
            }
        }
        for (shot in rows.asReversed()) {
            if (!permitted(shot.camera) || generation != ScreenshotSyncSettings.prefs(context).getLong("generation", 0)) return
            if (!TransferRuntime.connected.value) return
            ScreenshotSyncSettings.status.value = if (shot.camera) "正在同步新照片…" else "正在同步新截图…"
            if (upload(shot)) {
                completed.add(shot.id)
                retryAfter.remove(shot.id)
                while (completed.size > 256) completed.remove(completed.first())
                ScreenshotSyncSettings.status.value = if (shot.camera) "照片已同步到电脑" else "截图已同步到电脑"
            } else {
                // 加快发现新图不等于密集重发失败请求，失败的原图仍至少间隔五秒重试。
                retryAfter[shot.id] = android.os.SystemClock.elapsedRealtime() + 5000
                ScreenshotSyncSettings.status.value = "图片同步暂未成功，稍后自动重试"
                break
            }
        }
    }
    private data class Shot(val id: Long, val name: String, val size: Long, val camera: Boolean)
    private suspend fun upload(shot: Shot): Boolean {
        val token = session.token
        val uploadGeneration = generation
        val host = session.host.let { if (it.startsWith("http")) it else "http://$it" }
        val url = host.toHttpUrl().newBuilder().addPathSegments("api/files/upload")
            .addQueryParameter("t", token).addQueryParameter("name", shot.name)
            .addQueryParameter("id", "screenshot-${session.deviceId}-${shot.id}")
            .addQueryParameter("screenshot", "1").addQueryParameter("photo", if (shot.camera) "1" else "0").build()
        val body = object : RequestBody() {
            override fun contentType() = "application/octet-stream".toMediaType()
            override fun contentLength() = shot.size
            override fun writeTo(sink: BufferedSink) {
                context.contentResolver.openInputStream(ContentUris.withAppendedId(collection, shot.id))!!.use { input ->
                    val buffer = ByteArray(64*1024)
                    var remaining = shot.size
                    while (remaining > 0) {
                        if (!permitted(shot.camera) || session.token != token || uploadGeneration != ScreenshotSyncSettings.prefs(context).getLong("generation", 0)) throw IOException("截图同步已停止")
                        val n = input.read(buffer, 0, minOf(buffer.size.toLong(), remaining).toInt())
                        if (n < 0) throw IOException("截图尚未完整写入")
                        sink.write(buffer, 0, n); remaining -= n
                    }
                }
            }
        }
        return suspendCancellableCoroutine { continuation ->
            val call = client.newCall(Request.Builder().url(url).put(body).build())
            continuation.invokeOnCancellation { call.cancel() }
            call.enqueue(object : Callback {
                override fun onFailure(call: Call, e: IOException) { if (continuation.isActive) continuation.resume(false) }
                override fun onResponse(call: Call, response: Response) {
                    val success = response.use { it.isSuccessful }
                    if (continuation.isActive) continuation.resume(success)
                }
            })
        }
    }
    override fun close() {
        context.contentResolver.unregisterContentObserver(observer)
        ScreenshotSyncSettings.prefs(context).unregisterOnSharedPreferenceChangeListener(settingsListener)
        connectionJob.cancel()
        job.cancel(); client.dispatcher.cancelAll(); client.connectionPool.evictAll(); wake.close()
    }
}
