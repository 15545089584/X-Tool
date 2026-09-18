package com.xtool.collab.notification

import android.app.NotificationManager
import android.content.ComponentName
import android.content.Context
import android.os.Build
import android.os.Handler
import android.os.Looper
import android.os.SystemClock
import android.service.notification.NotificationListenerService
import androidx.core.app.NotificationManagerCompat

/** 只恢复已有授权；不修改系统通知使用权或用户同步偏好。 */
object NotificationListenerRecovery {
    private val retry = ListenerRetryGate()
    private val repair = ListenerRepairGate()
    private val handler = Handler(Looper.getMainLooper())
    private var pending = false
    fun hasAccess(context: Context): Boolean = if (Build.VERSION.SDK_INT >= 27)
        context.getSystemService(NotificationManager::class.java)
            .isNotificationListenerAccessGranted(ComponentName(context, PhoneNotificationListener::class.java))
        else NotificationManagerCompat.getEnabledListenerPackages(context).contains(context.packageName)

    @Synchronized fun ensureConnected(context: Context, manual: Boolean = false) {
        if (PhoneNotificationListener.listening) { retry.reset(); repair.connected(); return }
        val app = context.applicationContext
        val bridge = NotificationBridge(app)
        if (!bridge.paired || !bridge.enabled || bridge.connectionPaused || !hasAccess(app)) {
            retry.reset(); repair.connected(); return
        }
        val now = SystemClock.elapsedRealtime()
        if (pending || !retry.allow(now, manual)) return
        val component = ComponentName(app, PhoneNotificationListener::class.java)
        // 普通重绑持续无效时，Android 14+ 提供按组件解除旧绑定的公开接口。
        // 只重建本应用服务连接；权限、配对和逐应用选择保持原样。
        if (Build.VERSION.SDK_INT >= 34 && repair.shouldRepair(now)) {
            pending = true
            runCatching { NotificationListenerService.requestUnbind(component) }
            handler.postDelayed({
                synchronized(this) { pending = false }
                val current = NotificationBridge(app)
                if (current.paired && current.enabled && !current.connectionPaused && hasAccess(app)) {
                    runCatching { NotificationListenerService.requestRebind(component) }
                }
            }, 800)
            NotificationBridge.status.value = "正在重建系统通知监听连接"
            return
        }
        runCatching { NotificationListenerService.requestRebind(component) }
            .onSuccess { NotificationBridge.status.value = "通知权限已开启，正在恢复系统监听" }
            .onFailure { NotificationBridge.status.value = "系统监听暂未恢复，请点击重新连接监听" }
    }
}
