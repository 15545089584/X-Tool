package com.xtool.collab.service

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import com.xtool.collab.data.SessionStore

/** 开机后恢复已启用的可信连接；具体连通性仍由前台服务核验。 */
class AutoConnectReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent?) {
        val session = SessionStore(context)
        if (session.autoConnectEnabled && session.token.isNotBlank()) {
            runCatching { SyncForegroundService.start(context) }
        }
    }
}
