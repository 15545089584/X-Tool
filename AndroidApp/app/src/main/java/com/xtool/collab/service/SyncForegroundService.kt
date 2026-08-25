package com.xtool.collab.service

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.ContentValues
import android.content.Context
import android.content.Intent
import android.net.Uri
import android.os.Build
import android.os.IBinder
import android.provider.MediaStore
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
import kotlinx.coroutines.cancel
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.util.UUID

/** 常驻数据同步服务：恢复可信连接、接收电脑文件并提供双端进度。 */
class SyncForegroundService : Service() {
    companion object {
        private const val ChannelId = "collab_connection"
        private const val FileChannelId = "collab_file"
        private const val NotificationId = 1001
        private const val ExtraAutomatic = "automatic"

        fun start(context: Context, automatic: Boolean = true) {
            context.startForegroundService(
                Intent(context, SyncForegroundService::class.java).putExtra(ExtraAutomatic, automatic)
            )
        }

        fun stop(context: Context) {
            context.stopService(Intent(context, SyncForegroundService::class.java))
        }
    }

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Default)
    private lateinit var session: SessionStore
    private var automaticMode = true
    private var loopStarted = false

    override fun onCreate() {
        super.onCreate()
        session = SessionStore(applicationContext)
        createNotificationChannels()
        startForeground(NotificationId, buildConnectionNotification("正在查找已信任电脑"))
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        automaticMode = intent?.getBooleanExtra(ExtraAutomatic, true) ?: true
        if (!loopStarted) {
            loopStarted = true
            scope.launch { connectionLoop() }
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
            var connected = host.isNotBlank() && runCatching {
                withContext(Dispatchers.IO) { CollabApi(host).status(token, automatic = automaticMode) != null }
            }.getOrDefault(false)

            if (!connected && automaticMode && session.autoConnectEnabled) {
                TransferRuntime.updateConnection(false, "正在局域网内查找电脑")
                updateConnectionNotification("正在局域网内查找已信任电脑")
                val servers = runCatching {
                    withContext(Dispatchers.IO) { CollabApi.discover() }
                }.getOrDefault(emptyList())
                val candidates = servers
                    .filter { it.autoReconnectAllowed }
                    .sortedByDescending { it.serverId == session.serverId }
                for (candidate in candidates) {
                    val accepted = runCatching {
                        withContext(Dispatchers.IO) { CollabApi(candidate.host).status(token, automatic = true) != null }
                    }.getOrDefault(false)
                    if (!accepted) continue
                    host = candidate.host
                    session.host = candidate.host
                    session.serverId = candidate.serverId
                    session.serverName = candidate.serverName
                    connected = true
                    break
                }
            }

            if (!connected) {
                TransferRuntime.updateConnection(false, "等待电脑出现在同一局域网")
                updateConnectionNotification("等待已信任电脑上线")
                delay(4500)
                continue
            }

            val displayName = session.serverName.ifBlank { host }
            TransferRuntime.updateConnection(true, "已连接 $displayName")
            updateConnectionNotification("已连接 $displayName")
            receiveOutgoingFiles(host, token)
            delay(1800)
        }
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
            val uri = downloadToMediaStore(host, token, transferId, file)
            if (uri != null) notifyFileReceived(file.name, uri)
        }
    }

    private suspend fun downloadToMediaStore(host: String, token: String, transferId: String, file: RemoteFile): Uri? {
        return withContext(Dispatchers.IO) {
            try {
                val values = ContentValues().apply {
                    put(MediaStore.Downloads.DISPLAY_NAME, file.name)
                    put(MediaStore.Downloads.MIME_TYPE, "application/octet-stream")
                    if (Build.VERSION.SDK_INT >= 29) put(MediaStore.Downloads.RELATIVE_PATH, "Download/XTool")
                }
                val uri = contentResolver.insert(MediaStore.Downloads.EXTERNAL_CONTENT_URI, values) ?: return@withContext null
                val output = contentResolver.openOutputStream(uri) ?: run {
                    contentResolver.delete(uri, null, null)
                    return@withContext null
                }
                val ok = CollabApi(host).downloadFileStreaming(token, transferId, file, { output }) { done, total ->
                    TransferRuntime.updateTransfer(MobileTransfer(
                        transferId, file.name, "电脑 → 手机", done, total, "transferring", "正在接收"
                    ))
                    notifyTransferProgress(transferId, file.name, done, total)
                }
                if (!ok) {
                    contentResolver.delete(uri, null, null)
                    TransferRuntime.updateTransfer(MobileTransfer(
                        transferId, file.name, "电脑 → 手机", 0, file.size, "failed", "接收失败"
                    ))
                    return@withContext null
                }
                TransferRuntime.updateTransfer(MobileTransfer(
                    transferId, file.name, "电脑 → 手机", file.size, file.size, "completed", "已保存到 Download/XTool"
                ))
                getSystemService(NotificationManager::class.java).cancel(progressNotificationId(transferId))
                uri
            } catch (_: Exception) {
                TransferRuntime.updateTransfer(MobileTransfer(
                    transferId, file.name, "电脑 → 手机", 0, file.size, "failed", "接收中断"
                ))
                null
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
        val pending = PendingIntent.getActivity(
            this,
            name.hashCode(),
            Intent(Intent.ACTION_VIEW, uri).addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION),
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

    private fun progressNotificationId(transferId: String) = 3000 + (transferId.hashCode() and 0x0FFF)

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onDestroy() {
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
