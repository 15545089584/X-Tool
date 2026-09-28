package com.xtool.collab.service

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.ContentValues
import android.content.Context
import android.content.Intent
import android.media.AudioManager
import android.media.ToneGenerator
import android.net.Uri
import android.os.Build
import android.os.IBinder
import android.os.SystemClock
import android.provider.MediaStore
import android.webkit.MimeTypeMap
import androidx.core.app.NotificationCompat
import com.xtool.collab.MainActivity
import com.xtool.collab.data.CollabApi
import com.xtool.collab.data.MobileTransfer
import com.xtool.collab.data.RemoteFile
import com.xtool.collab.data.SessionStore
import com.xtool.collab.data.TransferRuntime
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.async
import kotlinx.coroutines.awaitAll
import kotlinx.coroutines.cancel
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.io.BufferedOutputStream
import java.io.FileOutputStream
import java.io.OutputStream
import java.nio.ByteBuffer
import java.nio.channels.FileChannel
import java.util.concurrent.ConcurrentHashMap
import java.util.UUID

/** 常驻数据同步服务：恢复可信连接、接收电脑文件并提供双端进度。 */
class SyncForegroundService : Service() {
    companion object {
        private const val ChannelId = "collab_connection"
        private const val FileChannelId = "collab_file"
        private const val NotificationId = 1001
        private const val ExtraAutomatic = "automatic"
        private const val MediaStoreBufferBytes = 2 * 1024 * 1024
        private const val ParallelDownloadThresholdBytes = 16L * 1024 * 1024
        private const val ParallelSegmentCount = 4
        private const val ProgressUiIntervalMilliseconds = 125L
        private const val ProgressNotificationIntervalMilliseconds = 750L
        private const val FailedTransferRetryDelayMilliseconds = 60_000L
        private const val ActiveTransferPreferencesName = "xtool_active_transfer"
        private const val ActiveTransferIdKey = "transfer_id"
        private const val ActiveTransferNameKey = "file_name"

        fun start(context: Context, automatic: Boolean = true) {
            com.xtool.collab.notification.NotificationBridge(context).connectionPaused = false
            context.startForegroundService(
                Intent(context, SyncForegroundService::class.java).putExtra(ExtraAutomatic, automatic)
            )
        }

        fun stop(context: Context) {
            com.xtool.collab.notification.NotificationBridge(context).connectionPaused = true
            context.stopService(Intent(context, SyncForegroundService::class.java))
        }
    }

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Default)
    private lateinit var session: SessionStore
    private var automaticMode = true
    private var calendarSync: com.xtool.collab.calendar.CalendarSync? = null
    private var screenshotSync: com.xtool.collab.screenshot.ScreenshotSync? = null
    private var loopStarted = false
    private var wasConnected = false
    private var lastLanDiscoveryAt = 0L
    private val failedTransferRetryAfter = ConcurrentHashMap<String, Long>()
    private val interruptedDownloadNames = ConcurrentHashMap.newKeySet<String>()
    private val activeTransferPreferences by lazy {
        getSharedPreferences(ActiveTransferPreferencesName, Context.MODE_PRIVATE)
    }

    override fun onCreate() {
        super.onCreate()
        TransferRuntime.initialize(applicationContext)
        session = SessionStore(applicationContext)
        // 监听恢复不依赖文件传输或网络探测完成，避免长传输阻塞重绑。
        scope.launch {
            while (isActive) {
                com.xtool.collab.notification.NotificationListenerRecovery.ensureConnected(applicationContext)
                delay(5_000)
            }
        }
        createNotificationChannels()
        startForeground(NotificationId, buildConnectionNotification("正在查找已信任电脑"))
        screenshotSync = com.xtool.collab.screenshot.ScreenshotSync(applicationContext, scope)
        calendarSync = com.xtool.collab.calendar.CalendarSync(applicationContext, scope)
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        automaticMode = intent?.getBooleanExtra(ExtraAutomatic, true) ?: true
        if (!loopStarted) {
            loopStarted = true
            scope.launch {
                activeTransferPreferences.getString(ActiveTransferNameKey, null)
                    ?.takeIf { it.isNotBlank() }
                    ?.let(interruptedDownloadNames::add)
                interruptedDownloadNames.addAll(cleanupStalePendingDownloads())
                connectionLoop()
            }
        }
        return START_STICKY
    }

    private suspend fun connectionLoop() {
        while (scope.isActive) {
            val token = session.token
            if (token.isBlank()) {
                TransferRuntime.updateConnection(false, "尚未配对")
                stopSelf()
                return
            }
            if (automaticMode && !session.autoConnectEnabled) {
                TransferRuntime.updateConnection(false, "自动连接已关闭")
                stopSelf()
                return
            }

            var host = session.host
            var connected = false
            var attemptedLanDiscovery = false

            // 热点提供方也有本地接口；不能仅凭 TRANSPORT_WIFI 判断能否发现电脑。
            val localNetworkAvailable = CollabApi.hasLocalDiscoveryNetwork()
            if (localNetworkAvailable && (!wasConnected ||
                    (session.host == session.tailscaleHost && SystemClock.elapsedRealtime() - lastLanDiscoveryAt >= 15_000))) {
                attemptedLanDiscovery = true
                lastLanDiscoveryAt = SystemClock.elapsedRealtime()
                discoverTrustedLanHost(token)?.let { discoveredHost ->
                    host = discoveredHost
                    connected = true
                }
            }

            if (!connected) {
                val savedHosts = listOf(session.lanHost, session.host, session.tailscaleHost)
                    .filter { it.isNotBlank() }
                    .distinct()
                for (candidateHost in savedHosts) {
                    val status = runCatching {
                        withContext(Dispatchers.IO) {
                            CollabApi(candidateHost).status(token, automatic = automaticMode)
                        }
                    }.getOrNull() ?: continue
                    if (session.serverId.isNotBlank() && status.optString("serverId") != session.serverId) continue
                    host = candidateHost
                    session.host = candidateHost
                    status.optString("tailscaleHost").takeIf { it.isNotBlank() }?.let { session.tailscaleHost = it }
                    connected = true
                    break
                }
            }

            // 手动点击连接也必须允许重新发现；自动连接开关只限制无人操作时的后台恢复。
            if (!connected && localNetworkAvailable && !attemptedLanDiscovery) {
                discoverTrustedLanHost(token)?.let { discoveredHost ->
                    host = discoveredHost
                    connected = true
                }
            }

            if (!connected) {
                wasConnected = false
                TransferRuntime.updateConnection(false, "等待电脑通过局域网或 Tailscale 上线")
                updateConnectionNotification("等待已信任电脑上线")
                delay(4500)
                continue
            }

            val displayName = session.serverName.ifBlank { host }
            TransferRuntime.updateConnection(true, "已连接 $displayName")
            updateConnectionNotification("已连接 $displayName")
            if (!wasConnected && !automaticMode) playConnectionTone()
            if (!wasConnected) com.xtool.collab.notification.PhoneNotificationListener.refresh()
            wasConnected = true
            receiveOutgoingFiles(host, token)
            delay(1800)
        }
    }

    /** 在 Wi-Fi 或热点本地接口上查找同一台已信任电脑，并只用既有令牌恢复会话。 */
    private suspend fun discoverTrustedLanHost(token: String): String? {
        TransferRuntime.updateConnection(false, "正在局域网内查找电脑")
        updateConnectionNotification("正在局域网内查找已信任电脑")
        val servers = runCatching {
            withContext(Dispatchers.IO) { CollabApi.discover() }
        }.getOrDefault(emptyList())
        val candidates = servers
            .filter { !automaticMode || it.autoReconnectAllowed }
            .filter { session.serverId.isBlank() || it.serverId == session.serverId }
            .sortedByDescending { it.serverId == session.serverId }
        for (candidate in candidates) {
            val status = runCatching {
                withContext(Dispatchers.IO) {
                    CollabApi(candidate.host).status(token, automatic = automaticMode)
                }
            }.getOrNull() ?: continue
            if (status.optString("serverId") != candidate.serverId) continue
            session.host = candidate.host
            session.lanHost = candidate.host
            session.tailscaleHost = candidate.tailscaleHost
            session.serverId = candidate.serverId
            session.serverName = candidate.serverName
            return candidate.host
        }
        return null
    }

    private suspend fun receiveOutgoingFiles(host: String, token: String) {
        val files = runCatching {
            withContext(Dispatchers.IO) { CollabApi(host).listOutgoingFiles(token) }
        }.getOrElse {
            TransferRuntime.updateConnection(false, "连接已中断，正在重试")
            return
        }
        for (file in files) {
            val transferId = file.id.ifBlank { UUID.randomUUID().toString().replace("-", "") }
            if (interruptedDownloadNames.contains(file.name)) {
                val discarded = withContext(Dispatchers.IO) {
                    CollabApi(host).cancelOutgoingDownload(token, transferId, file.name, discard = true)
                }
                if (!discarded) continue
                interruptedDownloadNames.remove(file.name)
                clearActiveDownload(file.name)
                TransferRuntime.updateTransfer(MobileTransfer(
                    transferId, file.name, "电脑 → 手机", 0, file.size, "failed", "上次传输已取消"
                ))
                continue
            }
            val now = SystemClock.elapsedRealtime()
            if ((failedTransferRetryAfter[transferId] ?: 0L) > now) continue
            rememberActiveDownload(transferId, file.name)
            val uri = try {
                downloadToMediaStore(host, token, transferId, file)
            } finally {
                clearActiveDownload(file.name)
            }
            if (uri != null) {
                failedTransferRetryAfter.remove(transferId)
                notifyFileReceived(file.name, uri)
            } else {
                failedTransferRetryAfter[transferId] = now + FailedTransferRetryDelayMilliseconds
            }
        }
    }

    private fun rememberActiveDownload(transferId: String, fileName: String) {
        activeTransferPreferences.edit()
            .putString(ActiveTransferIdKey, transferId)
            .putString(ActiveTransferNameKey, fileName)
            .commit()
    }

    private fun clearActiveDownload(fileName: String) {
        if (activeTransferPreferences.getString(ActiveTransferNameKey, null) != fileName) return
        activeTransferPreferences.edit()
            .remove(ActiveTransferIdKey)
            .remove(ActiveTransferNameKey)
            .apply()
    }

    /** 清理上次进程异常结束后仍处于 pending 状态的 X-Tool 半成品。 */
    private suspend fun cleanupStalePendingDownloads(): Set<String> = withContext(Dispatchers.IO) {
        if (Build.VERSION.SDK_INT < 29) return@withContext emptySet()
        val interruptedNames = linkedSetOf<String>()
        runCatching {
            val collection = MediaStore.Downloads.EXTERNAL_CONTENT_URI
            val projection = arrayOf(MediaStore.Downloads._ID, MediaStore.Downloads.DISPLAY_NAME)
            val selection = "${MediaStore.Downloads.RELATIVE_PATH} LIKE ? AND ${MediaStore.Downloads.IS_PENDING} = 1"
            contentResolver.query(collection, projection, selection, arrayOf("Download/XTool%"), null)?.use { cursor ->
                val idColumn = cursor.getColumnIndexOrThrow(MediaStore.Downloads._ID)
                val nameColumn = cursor.getColumnIndexOrThrow(MediaStore.Downloads.DISPLAY_NAME)
                while (cursor.moveToNext()) {
                    cursor.getString(nameColumn)?.takeIf { it.isNotBlank() }?.let(interruptedNames::add)
                    val staleUri = Uri.withAppendedPath(collection, cursor.getLong(idColumn).toString())
                    runCatching { contentResolver.delete(staleUri, null, null) }
                }
            }
        }
        interruptedNames
    }

    private suspend fun downloadToMediaStore(host: String, token: String, transferId: String, file: RemoteFile): Uri? {
        return withContext(Dispatchers.IO) {
            var createdUri: Uri? = null
            try {
                val mimeType = mimeTypeFor(file.name)
                val values = ContentValues().apply {
                    put(MediaStore.Downloads.DISPLAY_NAME, file.name)
                    put(MediaStore.Downloads.MIME_TYPE, mimeType)
                    if (Build.VERSION.SDK_INT >= 29) {
                        put(MediaStore.Downloads.RELATIVE_PATH, "Download/XTool")
                        put(MediaStore.Downloads.IS_PENDING, 1)
                    }
                }
                val uri = contentResolver.insert(MediaStore.Downloads.EXTERNAL_CONTENT_URI, values) ?: return@withContext null
                createdUri = uri
                val api = CollabApi(host)
                val ok = if (file.size >= ParallelDownloadThresholdBytes) {
                    downloadParallelToMediaStore(api, token, transferId, file, uri)
                } else {
                    downloadSingleToMediaStore(api, token, transferId, file, uri)
                }
                if (!ok) {
                    contentResolver.delete(uri, null, null)
                    createdUri = null
                    TransferRuntime.updateTransfer(MobileTransfer(
                        transferId, file.name, "电脑 → 手机", 0, file.size, "failed", "接收失败"
                    ))
                    return@withContext null
                }
                if (Build.VERSION.SDK_INT >= 29) {
                    contentResolver.update(uri, ContentValues().apply {
                        put(MediaStore.Downloads.IS_PENDING, 0)
                        put(MediaStore.Downloads.MIME_TYPE, mimeType)
                    }, null, null)
                }
                TransferRuntime.updateTransfer(MobileTransfer(
                    transferId, file.name, "电脑 → 手机", file.size, file.size, "completed", "已保存到 Download/XTool",
                    uri.toString()
                ))
                getSystemService(NotificationManager::class.java).cancel(progressNotificationId(transferId))
                createdUri = null
                uri
            } catch (_: Exception) {
                createdUri?.let { runCatching { contentResolver.delete(it, null, null) } }
                TransferRuntime.updateTransfer(MobileTransfer(
                    transferId, file.name, "电脑 → 手机", 0, file.size, "failed", "接收中断"
                ))
                null
            }
        }
    }

    private suspend fun downloadSingleToMediaStore(
        api: CollabApi,
        token: String,
        transferId: String,
        file: RemoteFile,
        uri: Uri
    ): Boolean {
        var lastProgressNotificationAt = 0L
        return api.downloadFileStreaming(
            token = token,
            transferId = transferId,
            file = file,
            openOutput = {
                val rawOutput = contentResolver.openOutputStream(uri) ?: error("无法创建接收文件")
                BufferedOutputStream(rawOutput, MediaStoreBufferBytes)
            },
            onProgress = { done, total ->
                TransferRuntime.updateTransfer(MobileTransfer(
                    transferId, file.name, "电脑 → 手机", done, total, "transferring", "正在接收"
                ))
                val now = SystemClock.elapsedRealtime()
                if (done >= total || now - lastProgressNotificationAt >= ProgressNotificationIntervalMilliseconds) {
                    lastProgressNotificationAt = now
                    notifyTransferProgress(transferId, file.name, done, total)
                }
            }
        )
    }

    private suspend fun downloadParallelToMediaStore(
        api: CollabApi,
        token: String,
        transferId: String,
        file: RemoteFile,
        uri: Uri
    ): Boolean {
        val segmentProgress = LongArray(ParallelSegmentCount)
        val progressSync = Any()
        var lastUiProgressAt = 0L
        var lastProgressNotificationAt = 0L
        return try {
            val descriptor = contentResolver.openFileDescriptor(uri, "rw") ?: error("无法打开接收文件")
            descriptor.use {
                FileOutputStream(descriptor.fileDescriptor).use { fileOutput ->
                    val channel = fileOutput.channel
                    channel.truncate(0)
                    val segmentsSucceeded = coroutineScope {
                        (0 until ParallelSegmentCount).map { index ->
                            async(Dispatchers.IO) {
                                val offset = file.size * index / ParallelSegmentCount
                                val end = file.size * (index + 1) / ParallelSegmentCount
                                val length = end - offset
                                api.downloadFileStreaming(
                                    token = token,
                                    transferId = transferId,
                                    file = file,
                                    offset = offset,
                                    requestedLength = length,
                                    parallelSegment = true,
                                    openOutput = { PositionedChannelOutputStream(channel, offset) },
                                    onProgress = { done, _ ->
                                        var aggregate = 0L
                                        var publishUi = false
                                        var publishNotification = false
                                        synchronized(progressSync) {
                                            segmentProgress[index] = done
                                            aggregate = segmentProgress.sum()
                                            val now = SystemClock.elapsedRealtime()
                                            if (aggregate >= file.size || now - lastUiProgressAt >= ProgressUiIntervalMilliseconds) {
                                                lastUiProgressAt = now
                                                publishUi = true
                                            }
                                            if (aggregate >= file.size ||
                                                now - lastProgressNotificationAt >= ProgressNotificationIntervalMilliseconds) {
                                                lastProgressNotificationAt = now
                                                publishNotification = true
                                            }
                                        }
                                        if (publishUi) {
                                            TransferRuntime.updateTransfer(MobileTransfer(
                                                transferId, file.name, "电脑 → 手机", aggregate, file.size,
                                                "transferring", "正在并行接收"
                                            ))
                                        }
                                        if (publishNotification) {
                                            notifyTransferProgress(transferId, file.name, aggregate, file.size)
                                        }
                                    }
                                )
                            }
                        }.awaitAll().all { it }
                    }
                    if (segmentsSucceeded) {
                        channel.force(false)
                    }
                    segmentsSucceeded
                }
            }.let { segmentsSucceeded ->
                if (!segmentsSucceeded) {
                    api.cancelOutgoingDownload(token, transferId, file.name)
                    false
                } else {
                    api.completeOutgoingDownload(token, transferId, file.name)
                }
            }
        } catch (_: Exception) {
            api.cancelOutgoingDownload(token, transferId, file.name)
            false
        }
    }

    /** 各分段共享同一文件通道，以显式位置写入避免 MediaStore 多描述符争用。 */
    private class PositionedChannelOutputStream(
        private val channel: FileChannel,
        startOffset: Long
    ) : OutputStream() {
        private var position = startOffset

        override fun write(value: Int) {
            val oneByte = byteArrayOf(value.toByte())
            write(oneByte, 0, 1)
        }

        override fun write(buffer: ByteArray, offset: Int, length: Int) {
            val bytes = ByteBuffer.wrap(buffer, offset, length)
            while (bytes.hasRemaining()) {
                val written = channel.write(bytes, position)
                if (written <= 0) error("接收文件写入中断")
                position += written
            }
        }
    }

    private fun notifyTransferProgress(transferId: String, name: String, done: Long, total: Long) {
        val percent = if (total > 0) ((done * 100 / total).coerceIn(0, 100)).toInt() else 0
        val notification = NotificationCompat.Builder(this, FileChannelId)
            .setSmallIcon(android.R.drawable.stat_sys_download)
            .setContentTitle("正在接收电脑文件")
            .setContentText("$name · $percent%")
            .setProgress(100, percent, total <= 0)
            .setOnlyAlertOnce(true)
            .setOngoing(true)
            .build()
        getSystemService(NotificationManager::class.java).notify(progressNotificationId(transferId), notification)
    }

    private fun notifyFileReceived(name: String, uri: Uri) {
        val mimeType = mimeTypeFor(name)
        val openIntent = Intent(Intent.ACTION_VIEW).apply {
            setDataAndType(uri, mimeType)
            addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
        }
        val pending = PendingIntent.getActivity(
            this,
            name.hashCode(),
            openIntent,
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE
        )
        val notification = NotificationCompat.Builder(this, FileChannelId)
            .setSmallIcon(android.R.drawable.stat_sys_download_done)
            .setContentTitle("电脑文件已接收")
            .setContentText("$name 已保存到 Download/XTool")
            .setContentIntent(pending)
            .setAutoCancel(true)
            .build()
        getSystemService(NotificationManager::class.java).notify(name.hashCode(), notification)
    }

    /** 根据扩展名向系统登记真实媒体类型，避免图片被文本查看器误打开。 */
    private fun mimeTypeFor(name: String): String {
        val extension = name.substringAfterLast('.', "").lowercase()
        if (extension.isBlank()) return "application/octet-stream"
        return MimeTypeMap.getSingleton().getMimeTypeFromExtension(extension)
            ?: when (extension) {
                "heic", "heif" -> "image/heic"
                "svg" -> "image/svg+xml"
                "md", "markdown" -> "text/markdown"
                "json" -> "application/json"
                else -> "application/octet-stream"
            }
    }

    private fun progressNotificationId(transferId: String) = 3000 + (transferId.hashCode() and 0x0FFF)

    private fun playConnectionTone() {
        runCatching {
            val tone = ToneGenerator(AudioManager.STREAM_NOTIFICATION, 75)
            tone.startTone(ToneGenerator.TONE_PROP_ACK, 140)
            scope.launch {
                delay(220)
                tone.release()
            }
        }
    }

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onDestroy() {
        calendarSync?.close()
        screenshotSync?.close()
        scope.cancel()
        TransferRuntime.updateConnection(false, "连接服务已停止")
        super.onDestroy()
    }

    private fun createNotificationChannels() {
        val manager = getSystemService(NotificationManager::class.java)
        manager.createNotificationChannel(NotificationChannel(ChannelId, "X-Tool 连接", NotificationManager.IMPORTANCE_LOW))
        manager.createNotificationChannel(NotificationChannel(FileChannelId, "X-Tool 文件传输", NotificationManager.IMPORTANCE_DEFAULT))
    }

    private fun updateConnectionNotification(text: String) {
        getSystemService(NotificationManager::class.java).notify(NotificationId, buildConnectionNotification(text))
    }

    private fun buildConnectionNotification(text: String): Notification {
        val pending = PendingIntent.getActivity(
            this,
            0,
            Intent(this, MainActivity::class.java),
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE
        )
        return NotificationCompat.Builder(this, ChannelId)
            .setSmallIcon(android.R.drawable.stat_notify_sync)
            .setContentTitle("X-Tool 协作连接")
            .setContentText(text)
            .setContentIntent(pending)
            .setOnlyAlertOnce(true)
            .setOngoing(true)
            .build()
    }
}
