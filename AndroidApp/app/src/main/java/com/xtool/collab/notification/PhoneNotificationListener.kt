package com.xtool.collab.notification

import android.app.Notification
import android.service.notification.NotificationListenerService
import android.service.notification.StatusBarNotification
import com.xtool.collab.data.SessionStore
import kotlinx.coroutines.*
import kotlinx.coroutines.channels.Channel
import org.json.JSONArray
import org.json.JSONObject
import java.util.UUID

/** 系统绑定的通知监听器；单发送协程与合并信号，不执行后台通知轮询。 */
class PhoneNotificationListener : NotificationListenerService() {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val signals = Channel<Unit>(Channel.CONFLATED)
    private val epoch = UUID.randomUUID().toString()
    private var sequence = 0L
    @Volatile private var connected = false
    override fun onCreate() {
        super.onCreate(); instance = this
        scope.launch {
            var backoff = 1000L
            while (isActive) {
                withTimeoutOrNull(if (backoff > 1000) backoff else 30_000L) { signals.receive() }
                delay(300)
                val bridge = NotificationBridge(this@PhoneNotificationListener)
                if (!bridge.paired) continue
                val enabled = bridge.enabled && connected && !bridge.connectionPaused
                val current = if (enabled) {
                    try { activeNotifications?.toList().orEmpty() } catch (_: Exception) { continue }
                } else emptyList()
                val items = JSONArray()
                current.asSequence().filter { it.packageName != packageName && bridge.mode(it.packageName) > 0 }
                    .filter { it.notification.flags and Notification.FLAG_GROUP_SUMMARY == 0 }
                    .sortedByDescending { it.postTime }.take(200).forEachIndexed { index, notification ->
                        val n = notification.notification
                        val extras = n.extras
                        val mode = bridge.mode(notification.packageName)
                        val app = runCatching { packageManager.getApplicationLabel(packageManager.getApplicationInfo(notification.packageName, 0)).toString() }.getOrDefault(notification.packageName)
                        val title = extras.getCharSequence(Notification.EXTRA_TITLE)?.toString().orEmpty().take(256)
                        val text = if (mode == 2) (extras.getCharSequence(Notification.EXTRA_BIG_TEXT)
                            ?: extras.getCharSequence(Notification.EXTRA_TEXT))?.toString().orEmpty().take(2000) else ""
                        val entry = JSONObject().put("key", notification.key.take(512)).put("package", notification.packageName.take(200))
                            .put("app", app.take(100)).put("title", title).put("text", text).put("postedAt", notification.postTime)
                        // 只附带最近十二条的缩略图，图片失败不能影响文字同步。
                        if (index < 12) NotificationAvatar.read(this@PhoneNotificationListener, n, notification.packageName)?.let { (data, kind) ->
                            entry.put("avatar", data).put("avatarKind", kind)
                        }
                        items.put(entry)
                    }
                val snapshot = JSONObject().put("deviceId", SessionStore(this@PhoneNotificationListener).deviceId)
                    .put("epoch", epoch).put("sequence", ++sequence).put("enabled", enabled).put("items", items)
                // 保留最新条目，UTF-8 编码后仍不得超过接收端的总请求预算。
                // 先舍弃图片，不能让新增头像挤掉原本可同步的文字。
                if (snapshot.toString().toByteArray(Charsets.UTF_8).size > 480 * 1024) {
                    for (index in 0 until items.length()) { items.getJSONObject(index).remove("avatar"); items.getJSONObject(index).remove("avatarKind") }
                }
                while (items.length() > 0 && snapshot.toString().toByteArray(Charsets.UTF_8).size > 480 * 1024) items.remove(items.length() - 1)
                backoff = if (bridge.send(snapshot.toString())) 1000L else (backoff * 2).coerceAtMost(30_000)
            }
        }
    }
    override fun onListenerConnected() { connected = true; signals.trySend(Unit) }
    override fun onListenerDisconnected() {
        connected = false; signals.trySend(Unit)
        NotificationBridge.status.value = "系统通知监听已断开，请检查通知使用权"
        NotificationListenerRecovery.ensureConnected(applicationContext)
    }
    override fun onNotificationPosted(sbn: StatusBarNotification?) { signals.trySend(Unit) }
    override fun onNotificationRemoved(sbn: StatusBarNotification?) { signals.trySend(Unit) }
    override fun onDestroy() { connected = false; if (instance === this) instance = null; scope.cancel(); super.onDestroy() }
    companion object {
        @Volatile private var instance: PhoneNotificationListener? = null
        val listening get() = instance?.connected == true
        fun refresh() { instance?.signals?.trySend(Unit) }
    }
}
