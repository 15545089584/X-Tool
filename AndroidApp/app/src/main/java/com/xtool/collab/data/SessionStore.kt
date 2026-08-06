package com.xtool.collab.data

import android.content.Context
import java.util.UUID

/** 本地保存配对会话：服务器地址、配对令牌与最近 PIN，仅用于本机局域网连接。 */
class SessionStore(context: Context) {
    private val prefs = context.getSharedPreferences("collab_session", Context.MODE_PRIVATE)

    var host: String
        get() = prefs.getString("host", "") ?: ""
        set(value) = prefs.edit().putString("host", value).apply()

    var token: String
        get() = prefs.getString("token", "") ?: ""
        set(value) = prefs.edit().putString("token", value).apply()

    var pin: String
        get() = prefs.getString("pin", "") ?: ""
        set(value) = prefs.edit().putString("pin", value).apply()

    /** 剪贴板自动同步总开关，前台服务与无障碍服务共用。 */
    var syncEnabled: Boolean
        get() = prefs.getBoolean("sync_enabled", true)
        set(value) = prefs.edit().putBoolean("sync_enabled", value).apply()

    /** 稳定设备标识：配对时交给电脑端，同一手机重复配对只计一台设备。 */
    val deviceId: String
        get() {
            val existing = prefs.getString("device_id", null)
            if (!existing.isNullOrBlank()) {
                return existing
            }
            val generated = UUID.randomUUID().toString()
            prefs.edit().putString("device_id", generated).apply()
            return generated
        }

    fun clear() {
        prefs.edit().clear().apply()
    }
}
