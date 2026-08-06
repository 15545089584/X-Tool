package com.xtool.collab.service

import android.accessibilityservice.AccessibilityService
import android.content.ClipboardManager
import android.content.Context
import android.view.accessibility.AccessibilityEvent
import com.xtool.collab.data.SessionStore

/**
 * 后台剪贴板监听：只订阅剪贴板变化（不声明读取窗口内容），
 * 用于在应用退到后台时仍能把手机复制的内容推送到电脑。
 */
class ClipboardAccessibilityService : AccessibilityService() {
    private val clipboardListener = ClipboardManager.OnPrimaryClipChangedListener {
        val session = SessionStore(this)
        val host = session.host
        val token = session.token
        if (session.syncEnabled && host.isNotBlank() && token.isNotBlank()) {
            ClipboardBridge.pushFromClipboard(this, host, token)
        }
    }

    override fun onServiceConnected() {
        super.onServiceConnected()
        val clipboard = getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
        clipboard.addPrimaryClipChangedListener(clipboardListener)
    }

    override fun onAccessibilityEvent(event: AccessibilityEvent?) {
        // 不处理界面事件；本服务仅用于剪贴板读取豁免与进程保活。
    }

    override fun onInterrupt() {
        // 系统中断时无需额外处理。
    }

    override fun onDestroy() {
        val clipboard = getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
        clipboard.removePrimaryClipChangedListener(clipboardListener)
        super.onDestroy()
    }
}
