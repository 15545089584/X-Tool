package com.xtool.collab.data

import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody
import org.json.JSONObject
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.URLEncoder
import java.util.concurrent.TimeUnit

/** 与电脑端协作服务对接的轻量客户端；所有调用必须在 IO 线程执行。 */
class CollabApi(private val host: String) {
    private val client = OkHttpClient.Builder()
        .connectTimeout(4, TimeUnit.SECONDS)
        .readTimeout(35, TimeUnit.MINUTES)
        .writeTimeout(35, TimeUnit.MINUTES)
        .build()

    private fun baseUrl() = "http://$host"

    fun pair(pin: String, deviceId: String, deviceName: String): PairResult {
        return try {
            val url = "${baseUrl()}/api/pair?pin=${encode(pin)}&device=${encode(deviceId)}&name=${encode(deviceName)}"
            val request = Request.Builder().url(url).build()
            client.newCall(request).execute().use { response ->
                if (!response.isSuccessful) {
                    return PairResult(error = if (response.code == 401) "配对码不正确" else "电脑端返回错误（${response.code}）")
                }
                val json = JSONObject(response.body?.string() ?: return PairResult(error = "电脑端响应为空"))
                val token = json.optString("token").takeIf { it.isNotBlank() }
                    ?: return PairResult(error = "电脑端未返回安全令牌")
                PairResult(
                    token = token,
                    host = json.optString("host", host),
                    serverId = json.optString("serverId"),
                    serverName = json.optString("serverName")
                )
            }
        } catch (_: java.io.IOException) {
            PairResult(error = "无法连接电脑，请检查双方网络与防火墙")
        } catch (_: Exception) {
            PairResult(error = "配对请求异常，请重试")
        }
    }

    fun status(token: String, automatic: Boolean = false): JSONObject? {
        val suffix = if (automatic) "&auto=1" else ""
        val request = Request.Builder().url("${baseUrl()}/api/status?t=${encode(token)}$suffix").build()
        client.newCall(request).execute().use { response ->
            if (!response.isSuccessful) return null
            return JSONObject(response.body?.string() ?: return null)
        }
    }

    fun logout(token: String): Boolean = try {
        val request = Request.Builder().url("${baseUrl()}/api/logout?t=${encode(token)}").build()
        client.newCall(request).execute().use { it.isSuccessful }
    } catch (_: Exception) {
        false
    }

    fun listOutgoingFiles(token: String): List<RemoteFile> {
        val request = Request.Builder().url("${baseUrl()}/api/files/list?t=${encode(token)}&dir=outgoing").build()
        client.newCall(request).execute().use { response ->
            if (!response.isSuccessful) return emptyList()
            val array = JSONObject(response.body?.string() ?: return emptyList()).optJSONArray("files") ?: return emptyList()
            return (0 until array.length()).mapNotNull { index ->
                val item = array.optJSONObject(index) ?: return@mapNotNull null
                RemoteFile(item.optString("id"), item.optString("name"), item.optLong("size"), item.optString("at"))
            }
        }
    }

    fun uploadFileStreaming(
        token: String,
        transferId: String,
        name: String,
        openInput: () -> java.io.InputStream,
        totalBytes: Long,
        onProgress: (Long, Long) -> Unit
    ): Boolean {
        val body = object : RequestBody() {
            override fun contentType() = "application/octet-stream".toMediaType()
            override fun contentLength() = totalBytes
            override fun writeTo(sink: okio.BufferedSink) {
                val buffer = ByteArray(128 * 1024)
                var uploaded = 0L
                openInput().use { input ->
                    while (true) {
                        val read = input.read(buffer)
                        if (read < 0) break
                        sink.write(buffer, 0, read)
                        uploaded += read
                        onProgress(uploaded, totalBytes)
                    }
                }
            }
        }
        val url = "${baseUrl()}/api/files/upload?t=${encode(token)}&id=${encode(transferId)}&name=${encode(name)}"
        val request = Request.Builder().url(url).put(body).build()
        client.newCall(request).execute().use { return it.isSuccessful }
    }

    fun downloadFileStreaming(
        token: String,
        transferId: String,
        file: RemoteFile,
        openOutput: () -> java.io.OutputStream,
        onProgress: (Long, Long) -> Unit
    ): Boolean {
        val url = "${baseUrl()}/api/files/download?t=${encode(token)}&id=${encode(transferId)}&dir=outgoing&name=${encode(file.name)}"
        val request = Request.Builder().url(url).build()
        client.newCall(request).execute().use { response ->
            if (!response.isSuccessful) return false
            val input = response.body?.byteStream() ?: return false
            val total = response.body?.contentLength()?.takeIf { it >= 0 } ?: file.size
            input.use { source ->
                openOutput().use { target ->
                    val buffer = ByteArray(128 * 1024)
                    var downloaded = 0L
                    while (true) {
                        val read = source.read(buffer)
                        if (read < 0) break
                        target.write(buffer, 0, read)
                        downloaded += read
                        onProgress(downloaded, total)
                    }
                    target.flush()
                }
            }
            return true
        }
    }

    companion object {
        private const val DiscoveryPort = 18121
        private const val DiscoveryRequest = "XTOOL_DISCOVER_V1"

        /** 通过局域网广播查找电脑，不携带任何配对令牌。 */
        fun discover(timeoutMs: Int = 1800): List<DiscoveredServer> {
            val found = linkedMapOf<String, DiscoveredServer>()
            DatagramSocket().use { socket ->
                socket.broadcast = true
                socket.soTimeout = 350
                val requestBytes = DiscoveryRequest.toByteArray(Charsets.UTF_8)
                val request = DatagramPacket(requestBytes, requestBytes.size, InetAddress.getByName("255.255.255.255"), DiscoveryPort)
                socket.send(request)
                val deadline = System.currentTimeMillis() + timeoutMs
                val responseBuffer = ByteArray(2048)
                while (System.currentTimeMillis() < deadline) {
                    try {
                        val response = DatagramPacket(responseBuffer, responseBuffer.size)
                        socket.receive(response)
                        val json = JSONObject(String(response.data, response.offset, response.length, Charsets.UTF_8))
                        if (json.optString("service") != "xtool-collaboration-v1") continue
                        val discoveredHost = json.optString("host")
                        val serverId = json.optString("serverId")
                        if (discoveredHost.isBlank() || serverId.isBlank()) continue
                        found[serverId] = DiscoveredServer(
                            host = discoveredHost,
                            serverId = serverId,
                            serverName = json.optString("serverName", "X-Tool 电脑"),
                            autoReconnectAllowed = json.optBoolean("autoReconnectAllowed", true)
                        )
                    } catch (_: java.net.SocketTimeoutException) {
                        // 在总等待时间内继续接收其它网卡或电脑的响应。
                    }
                }
            }
            return found.values.toList()
        }

        private fun encode(value: String) = URLEncoder.encode(value, "UTF-8")
    }
}

data class PairResult(
    val token: String? = null,
    val host: String = "",
    val serverId: String = "",
    val serverName: String = "",
    val error: String? = null
)

data class RemoteFile(val id: String, val name: String, val size: Long, val at: String)

data class DiscoveredServer(
    val host: String,
    val serverId: String,
    val serverName: String,
    val autoReconnectAllowed: Boolean
)
