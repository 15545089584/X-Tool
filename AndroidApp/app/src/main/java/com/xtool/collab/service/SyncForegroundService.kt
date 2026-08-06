package com.xtool.collab.service

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.ClipboardManager
import android.content.Context
import android.content.Intent
import android.os.IBinder
import androidx.core.app.NotificationCompat
import com.xtool.collab.MainActivity
import com.xtool.collab.R
import com.xtool.collab.data.CollabApi
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import android.content.ContentValues
import android.net.Uri
import android.os.Build
import android.provider.MediaStore
import com.xtool.collab.data.RemoteFile

/** 常驻前台服务：后台轮询电脑端剪贴板并写入手机剪贴板，保持同步连接。 */
class SyncForegroundService : Service() {
    companion object {
        private const val ChannelId = "collab_sync"
        private const val FileChannelId = "collab_file"
        private const val NotificationId = 1001
        private const val ExtraHost = "host"
        private const val ExtraToken = "token"

        fun start(context: Context, host: String, token: String) {
            val intent = Intent(context, SyncForegroundService::class.java)
                .putExtra(ExtraHost, host)
                .putExtra(ExtraToken, token)
            context.startForegroundService(intent)
        }

        fun stop(context: Context) {
            context.stopService(Intent(context, SyncForegroundService::class.java))
        }
    }

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Default)
    private var lastSeqValue = 0L
    private var host = ""
    private var token = ""
    private val knownOutgoingFiles = mutableSetOf<String>()
    private val clipboardListener = ClipboardManager.OnPrimaryClipChangedListener {
        // 前台服务作为推送兜底：App 在前台时手机复制也能推送到电脑
        // （后台读取豁免仍由无障碍服务负责）。
        ClipboardBridge.pushFromClipboard(this@SyncForegroundService, host, token)
    }

    override fun onCreate() {
        super.onCreate()
        createNotificationChannel()
        startForeground(NotificationId, buildNotification("剪贴板同步中"))
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        val host = intent?.getStringExtra(ExtraHost).orEmpty()
        val token = intent?.getStringExtra(ExtraToken).orEmpty()
        if (host.isBlank() || token.isBlank()) {
            stopSelf()
            return START_NOT_STICKY
        }
        this.host = host
        this.token = token
        // 记录当前发送目录文件，避免服务重启后重复下载。
        scope.launch {
            val initialFiles = runCatching<List<RemoteFile>> {
                withContext(Dispatchers.IO) { CollabApi(host).listOutgoingFiles(token) }
            }
                .getOrDefault(emptyList())
            initialFiles.forEach { knownOutgoingFiles.add(it.name) }
        }
        val clipboard = getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
        clipboard.addPrimaryClipChangedListener(clipboardListener)
        scope.launch {
            while (isActive) {
                runCatching {
                    val current = withContext(Dispatchers.IO) {
                        ClipboardBridge.pullAndWrite(this@SyncForegroundService, host, token, lastSeqValue)
                    }
                    lastSeqValue = current
                }
                    .onSuccess { ClipboardBridge.SyncState.update { connected = true } }
                    .onFailure { ClipboardBridge.SyncState.update { connected = false } }
                delay(1000)
            }
        }
        // 文件自动下载：每 5 秒检查电脑发送目录的新文件。
        scope.launch {
            while (isActive) {
                runCatching {
                    val files = withContext(Dispatchers.IO) { CollabApi(host).listOutgoingFiles(token) }
                    for (file in files) {
                        if (file.name in knownOutgoingFiles) continue
                        val uri = downloadToMediaStore(file)
                        if (uri != null) {
                            knownOutgoingFiles.add(file.name)
                            notifyFileReceived(file.name, uri)
                        }
                    }
                }
                delay(5000)
            }
        }
        return START_STICKY
    }

    private fun downloadToMediaStore(file: RemoteFile): Uri? {
        return try {
            val values = ContentValues().apply {
                put(MediaStore.Downloads.DISPLAY_NAME, file.name)
                put(MediaStore.Downloads.MIME_TYPE, "application/octet-stream")
                if (Build.VERSION.SDK_INT >= 29) {
                    put(MediaStore.Downloads.RELATIVE_PATH, "Download/XTool")
                }
            }
            val uri: Uri = contentResolver.insert(MediaStore.Downloads.EXTERNAL_CONTENT_URI, values) ?: return null
            val ok = contentResolver.openOutputStream(uri)?.let { output ->
                CollabApi(host).downloadFileStreaming(token, file.name) { output }
            } ?: false
            if (!ok) {
                contentResolver.delete(uri, null, null)
                return null
            }
            uri
        } catch (_: Exception) {
            null
        }
    }

    private fun notifyFileReceived(name: String, uri: Uri) {
        val channel = NotificationChannel(FileChannelId, "X-Tool 文件传输", NotificationManager.IMPORTANCE_DEFAULT)
        getSystemService(NotificationManager::class.java).createNotificationChannel(channel)
        val openIntent = Intent(Intent.ACTION_VIEW, uri)
            .addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
        val pending = PendingIntent.getActivity(
            this,
            0,
            openIntent,
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE
        )
        val notification = NotificationCompat.Builder(this, FileChannelId)
            .setSmallIcon(android.R.drawable.stat_sys_download_done)
            .setContentTitle("收到电脑文件")
            .setContentText("$name 已保存到手机下载/XTool 目录")
            .setContentIntent(pending)
            .setAutoCancel(true)
            .build()
        getSystemService(NotificationManager::class.java).notify(2001, notification)
    }

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onDestroy() {
        val clipboard = getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
        clipboard.removePrimaryClipChangedListener(clipboardListener)
        scope.cancel()
        super.onDestroy()
    }

    private fun createNotificationChannel() {
        val channel = NotificationChannel(
            ChannelId,
            "X-Tool 协作同步",
            NotificationManager.IMPORTANCE_LOW
        )
        getSystemService(NotificationManager::class.java).createNotificationChannel(channel)
    }

    private fun buildNotification(text: String): Notification {
        val contentIntent = PendingIntent.getActivity(
            this,
            0,
            Intent(this, MainActivity::class.java),
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE
        )
        return NotificationCompat.Builder(this, ChannelId)
            .setSmallIcon(android.R.drawable.stat_notify_sync)
            .setContentTitle("X-Tool 协作")
            .setContentText(text)
            .setContentIntent(contentIntent)
            .setOngoing(true)
            .build()
    }

}
