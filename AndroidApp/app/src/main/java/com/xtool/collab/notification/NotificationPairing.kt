package com.xtool.collab.notification

import org.json.JSONObject
import java.net.URI
import java.util.Base64

/** 身份只来自用户扫描的二维码；片段不发送给旧 HTTP 配对端点。 */
data class NotificationPairing(val host: String, val fingerprint: String, val secret: String) {
    companion object {
        fun enabledAfterBinding(previous: Boolean?): Boolean = previous ?: true

        fun parse(scanned: String, expectedServerId: String): NotificationPairing {
            require(scanned.length <= 4096 && expectedServerId.isNotBlank())
            val payload = if (scanned.startsWith("http://")) {
                val uri = URI(scanned)
                require(uri.path == "/pair" && uri.userInfo == null)
                val fragment = uri.rawFragment.orEmpty()
                require(fragment.startsWith("xtool-notify=")) { "此配对码尚未包含通知身份，请更新电脑端" }
                String(Base64.getUrlDecoder().decode(fragment.removePrefix("xtool-notify=")), Charsets.UTF_8)
            } else scanned // 兼容已发出的通知专用二维码。
            val json = JSONObject(payload)
            require(json.getString("kind") == "xtool-notifications-v1")
            require(json.getString("serverId") == expectedServerId) { "二维码不属于当前连接的电脑" }
            require(json.getInt("port") == 18122)
            val host = json.getString("host")
            val octets = host.split('.')
            require(octets.size == 4 && octets.all { it.matches(Regex("[0-9]{1,3}")) && it.toInt() in 0..255 })
            val fingerprint = json.getString("fingerprint")
            val secret = json.getString("secret")
            require(fingerprint.matches(Regex("[A-Fa-f0-9]{64}")) && secret.matches(Regex("[A-Fa-f0-9]{64}")))
            return NotificationPairing(host, fingerprint, secret)
        }
    }
}
