package com.xtool.collab.notification

import android.content.Context
import com.xtool.collab.data.SessionStore
import kotlinx.coroutines.flow.MutableStateFlow
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import org.json.JSONObject
import java.net.URI
import java.security.MessageDigest
import java.security.SecureRandom
import java.security.cert.CertificateException
import java.security.cert.X509Certificate
import java.util.concurrent.TimeUnit
import javax.net.ssl.SSLContext
import javax.net.ssl.X509TrustManager

/** 通知专用配对。凭据仅存应用私有目录，禁用备份；服务器证书必须与扫码指纹精确一致。 */
class NotificationBridge(private val context: Context) {
    private val prefs = context.getSharedPreferences("notification_bridge", Context.MODE_PRIVATE)
    var enabled: Boolean
        get() = prefs.getBoolean("enabled", false)
        set(value) { prefs.edit().putBoolean("enabled", value).apply() }
    // 手动断开只暂停传输，不覆盖用户选择的通知开关。
    var connectionPaused: Boolean
        get() = prefs.getBoolean("connection_paused", false)
        set(value) { prefs.edit().putBoolean("connection_paused", value).apply(); PhoneNotificationListener.refresh() }
    val paired get() = prefs.getString("fingerprint", "").orEmpty().isNotBlank() &&
        SessionStore(context).let { it.token.isNotBlank() && it.serverId == prefs.getString("serverId", "") }
    fun mode(packageName: String): Int = prefs.getInt("app:$packageName", 0)
    fun setMode(packageName: String, mode: Int) { prefs.edit().putInt("app:$packageName", mode.coerceIn(0, 2)).apply() }
    fun bind(payload: String) {
        val session = SessionStore(context)
        require(session.token.isNotBlank()) { "请先连接协作中心" }
        val identity = NotificationPairing.parse(payload, session.serverId)
        prefs.edit().putString("host", identity.host).putString("fingerprint", identity.fingerprint).putString("secret", identity.secret)
            .putString("serverId", session.serverId)
            // 首次安全配对默认开启；升级、重新配对不能覆盖用户关闭的选择。
            .putBoolean("enabled", NotificationPairing.enabledAfterBinding(if (prefs.contains("enabled")) enabled else null)).apply()
        PhoneNotificationListener.refresh()
    }
    fun forget() { prefs.edit().clear().apply(); PhoneNotificationListener.refresh() }

    fun send(payload: String): Boolean = sendTo(payload, false)
    fun sendCalendar(payload: String, receive: (JSONObject) -> Unit = {}): Boolean = sendTo(payload, true, receive)

    private fun sendTo(payload: String, calendar: Boolean, receive: (JSONObject) -> Unit = {}): Boolean {
        val status = if (calendar) calendarStatus else Companion.status
        val session = SessionStore(context)
        if (!paired || session.token.isBlank() || session.serverId != prefs.getString("serverId", "")) {
            status.value = "请先绑定当前电脑的通知通道"; return false
        }
        val expected = prefs.getString("fingerprint", "").orEmpty().uppercase()
        val trust = object : X509TrustManager {
            override fun getAcceptedIssuers(): Array<X509Certificate> = emptyArray()
            override fun checkClientTrusted(chain: Array<X509Certificate>, authType: String) = throw CertificateException()
            override fun checkServerTrusted(chain: Array<X509Certificate>, authType: String) {
                val certificate = chain.firstOrNull() ?: throw CertificateException()
                certificate.checkValidity()
                val hash = MessageDigest.getInstance("SHA-256").digest(certificate.encoded).joinToString("") { "%02X".format(it) }
                if (!MessageDigest.isEqual(hash.toByteArray(), expected.toByteArray())) throw CertificateException("证书指纹不符")
            }
        }
        val ssl = SSLContext.getInstance("TLS").apply { init(null, arrayOf(trust), SecureRandom()) }
        val client = OkHttpClient.Builder().sslSocketFactory(ssl.socketFactory, trust)
            // IP 随热点变化；身份由扫码证书固定验证，绝不接受任意自签名证书。
            .hostnameVerifier { _, tls -> runCatching {
                val certificate = tls.peerCertificates.first() as X509Certificate
                trust.checkServerTrusted(arrayOf(certificate), "RSA"); true
            }.getOrDefault(false) }
            .followRedirects(false).followSslRedirects(false).proxy(java.net.Proxy.NO_PROXY)
            .connectTimeout(2, TimeUnit.SECONDS).readTimeout(4, TimeUnit.SECONDS).callTimeout(6, TimeUnit.SECONDS).build()
        val hosts = listOf(session.host, session.lanHost, session.tailscaleHost).filter { it.isNotBlank() }
            .mapNotNull { runCatching { URI("http://$it").host }.getOrNull() }
            .plus(prefs.getString("host", "").orEmpty()).filter { it.isNotBlank() }.distinct().take(4)
        try {
            for (host in hosts) {
                try {
                    val request = Request.Builder().url("https://$host:18122/api/v2/" + (if (calendar) "calendar" else "notifications") + "/snapshot")
                        .header("Authorization", "Bearer " + prefs.getString("secret", ""))
                        .post(payload.toRequestBody("application/json; charset=utf-8".toMediaType())).build()
                    client.newCall(request).execute().use { response ->
                        if (response.code == 200) {
                            if (calendar) {
                                val output = java.io.ByteArrayOutputStream()
                                response.body?.byteStream()?.use { input ->
                                    val buffer = ByteArray(4096)
                                    while (output.size() <= 65536) {
                                        val count = input.read(buffer, 0, minOf(buffer.size, 65537 - output.size()))
                                        if (count < 0) break
                                        output.write(buffer, 0, count)
                                    }
                                }
                                val bytes = output.toByteArray()
                                check(bytes.size <= 65536)
                                receive(if (bytes.isEmpty()) JSONObject() else JSONObject(String(bytes, Charsets.UTF_8)))
                            }
                            status.value = "已加密同步 · " + java.text.SimpleDateFormat("HH:mm:ss", java.util.Locale.getDefault()).format(java.util.Date()); return true }
                        if (response.code == 401) { status.value = "通知绑定已撤销，请重新扫码"; return false }
                        if (response.code == 409) { status.value = "电脑已暂停接收"; return false }
                    }
                } catch (_: Exception) { /* 不输出正文、地址中的凭据或证书内容。 */ }
            }
            status.value = "通知通道未连接，正在等待网络恢复"; return false
        } finally { client.connectionPool.evictAll(); client.dispatcher.executorService.shutdown() }
    }
    companion object { val status = MutableStateFlow("尚未连接通知通道"); val calendarStatus = MutableStateFlow("尚未同步日程") }
}
