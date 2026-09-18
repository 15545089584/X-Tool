package com.xtool.collab.data

import android.content.Context
import java.util.UUID

/** 本地保存配对会话与自动连接偏好；敏感令牌只保存在应用私有目录。 */
class SessionStore(context: Context) {
    private val prefs = context.getSharedPreferences("collab_session", Context.MODE_PRIVATE)

    var host: String
        get() = prefs.getString("host", "") ?: ""
        set(value) = prefs.edit().putString("host", value).apply()

    var lanHost: String
        get() = prefs.getString("lan_host", "") ?: ""
        set(value) = prefs.edit().putString("lan_host", value).apply()

    var tailscaleHost: String
        get() = prefs.getString("tailscale_host", "") ?: ""
        set(value) = prefs.edit().putString("tailscale_host", value).apply()

    var token: String
        get() = prefs.getString("token", "") ?: ""
        set(value) = prefs.edit().putString("token", value).apply()

    var pin: String
        get() = prefs.getString("pin", "") ?: ""
        set(value) = prefs.edit().putString("pin", value).apply()

    var serverId: String
        get() = prefs.getString("server_id", "") ?: ""
        set(value) = prefs.edit().putString("server_id", value).apply()

    var serverName: String
        get() = prefs.getString("server_name", "") ?: ""
        set(value) = prefs.edit().putString("server_name", value).apply()

    /** 手机端是否允许在网络恢复后主动查找已信任电脑。 */
    var autoConnectEnabled: Boolean
        get() = prefs.getBoolean("auto_connect_enabled", true)
        set(value) = prefs.edit().putBoolean("auto_connect_enabled", value).apply()

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

    fun clearSession() {
        prefs.edit()
            .remove("host")
            .remove("lan_host")
            .remove("tailscale_host")
            .remove("token")
            .remove("pin")
            .remove("server_id")
            .remove("server_name")
            .apply()
    }
}
