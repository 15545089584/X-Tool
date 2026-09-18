package com.xtool.collab.notification

import android.app.Activity
import android.app.Instrumentation
import android.content.ComponentName
import android.os.Build
import android.os.Bundle
import android.os.SystemClock
import android.service.notification.NotificationListenerService

/** 真机仅验证本应用监听重绑，不修改通知使用权，不读取或输出消息内容。 */
class RecoveryInstrumentation : Instrumentation() {
    override fun onCreate(arguments: Bundle?) { super.onCreate(arguments); start() }
    override fun onStart() {
        val result = Bundle()
        try {
            check(Build.VERSION.SDK_INT >= 34)
            check(NotificationListenerRecovery.hasAccess(targetContext)) { "通知使用权未开启" }
            val bridge = NotificationBridge(targetContext)
            check(bridge.paired && bridge.enabled && !bridge.connectionPaused) { "同步未启用" }
            fun awaitBinding() {
                val deadline = SystemClock.elapsedRealtime() + 70_000
                while (!PhoneNotificationListener.listening && SystemClock.elapsedRealtime() < deadline) {
                    NotificationListenerRecovery.ensureConnected(targetContext)
                    SystemClock.sleep(1000)
                }
                check(PhoneNotificationListener.listening) { "监听恢复超时" }
            }
            awaitBinding()
            repeat(2) {
                NotificationListenerService.requestUnbind(ComponentName(targetContext, PhoneNotificationListener::class.java))
                SystemClock.sleep(1500)
                check(!PhoneNotificationListener.listening) { "未观察到监听断开" }
                awaitBinding()
                check(NotificationListenerRecovery.hasAccess(targetContext)) { "权限状态改变" }
            }
            result.putString("result", "PASS: two listener disconnect/rebind cycles; permission unchanged")
            finish(Activity.RESULT_OK, result)
        } catch (error: Throwable) {
            result.putString("result", "FAIL: " + error.javaClass.simpleName + ": " + error.message)
            finish(Activity.RESULT_CANCELED, result)
        }
    }
}
