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
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

/** 常驻前台服务：后台轮询电脑端剪贴板并写入手机剪贴板，保持同步连接。 */
class SyncForegroundService : Service() {
    companion object {
        private const val ChannelId = "collab_sync"
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
                delay(3000)
            }
        }
        return START_STICKY
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
