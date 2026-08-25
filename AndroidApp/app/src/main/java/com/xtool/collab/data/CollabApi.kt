package com.xtool.collab.data

import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody
import org.json.JSONObject
import java.io.BufferedInputStream
import java.io.ByteArrayOutputStream
import java.io.InputStream
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.Socket
import java.net.URI
import java.net.URLEncoder
import java.nio.charset.StandardCharsets
import java.util.concurrent.TimeUnit

/** 与电脑端协作服务对接的轻量客户端；所有调用必须在 IO 线程执行。 */
class CollabApi(private val host: String) {
    private val client = SharedClient

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

    /** 仅结束当前在线状态，不删除电脑端信任关系。 */
    fun disconnect(token: String): Boolean = try {
        val request = Request.Builder().url("${baseUrl()}/api/disconnect?t=${encode(token)}").build()
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
                val buffer = ByteArray(TRANSFER_BATCH_BYTES)
                var uploaded = 0L
                var lastProgressAt = 0L
                openInput().use { input ->
                    while (uploaded < totalBytes) {
                        val expected = minOf(buffer.size.toLong(), totalBytes - uploaded).toInt()
                        val read = readBatch(input, buffer, expected)
                        if (read <= 0) break
                        sink.write(buffer, 0, read)
                        uploaded += read
                        val now = System.nanoTime()
                        if (uploaded >= totalBytes || now - lastProgressAt >= PROGRESS_INTERVAL_NANOS) {
                            lastProgressAt = now
                            onProgress(uploaded, totalBytes)
                        }
                    }
                }
            }
        }
        val url = "${baseUrl()}/api/files/upload?t=${encode(token)}&id=${encode(transferId)}&name=${encode(name)}"
        val request = Request.Builder().url(url).put(body).build()
        client.newCall(request).execute().use { return it.isSuccessful }
    }

    suspend fun downloadFileStreaming(
        token: String,
        transferId: String,
        file: RemoteFile,
        offset: Long = 0,
        requestedLength: Long = file.size,
        parallelSegment: Boolean = false,
        openOutput: () -> java.io.OutputStream,
        onProgress: (Long, Long) -> Unit
    ): Boolean {
        val endpoint = URI(baseUrl())
        val port = if (endpoint.port > 0) endpoint.port else 80
        val socket = Socket()
        return try {
            socket.tcpNoDelay = true
            socket.receiveBufferSize = SOCKET_BUFFER_BYTES
            socket.connect(InetSocketAddress(endpoint.host, port), CONNECT_TIMEOUT_MILLISECONDS)
            socket.soTimeout = SOCKET_IDLE_TIMEOUT_MILLISECONDS

            val segmentQuery = if (parallelSegment) {
                "&parallel=1&offset=$offset&length=$requestedLength"
            } else {
                ""
            }
            val requestPath = "/api/files/download?t=${encode(token)}&id=${encode(transferId)}&dir=outgoing&name=${encode(file.name)}$segmentQuery"
            val hostHeader = if (endpoint.port > 0) "${endpoint.host}:$port" else endpoint.host
            val requestText = "GET $requestPath HTTP/1.1\r\nHost: $hostHeader\r\nConnection: close\r\n\r\n"
            val requestOutput = socket.getOutputStream().buffered(16 * 1024)
            requestOutput.write(requestText.toByteArray(StandardCharsets.US_ASCII))
            requestOutput.flush()

            val source = BufferedInputStream(socket.getInputStream(), HTTP_INPUT_BUFFER_BYTES)
            val responseHead = readHttpResponseHead(source) ?: return false
            val expectedStatus = if (parallelSegment) 206 else 200
            if (responseHead.statusCode != expectedStatus) return false
            val total = responseHead.contentLength.takeIf { it >= 0 } ?: requestedLength

            var written = 0L
            openOutput().use { target ->
                val buffer = ByteArray(TRANSFER_BATCH_BYTES)
                var lastProgressAt = 0L
                while (total <= 0 || written < total) {
                    val expected = if (total > 0) {
                        minOf(buffer.size.toLong(), total - written).toInt()
                    } else {
                        buffer.size
                    }
                    val read = readBatch(source, buffer, expected)
                    if (read <= 0) break
                    target.write(buffer, 0, read)
                    written += read
                    val now = System.nanoTime()
                    if (written >= total || now - lastProgressAt >= PROGRESS_INTERVAL_NANOS) {
                        lastProgressAt = now
                        onProgress(written, total)
                    }
                }
                target.flush()
            }
            total <= 0 || written == total
        } finally {
            runCatching { socket.close() }
        }
    }

    fun completeOutgoingDownload(token: String, transferId: String, name: String): Boolean = try {
        val url = "${baseUrl()}/api/files/complete?t=${encode(token)}&id=${encode(transferId)}&name=${encode(name)}"
        val request = Request.Builder().url(url).build()
        client.newCall(request).execute().use { it.isSuccessful }
    } catch (_: Exception) {
        false
    }

    fun cancelOutgoingDownload(token: String, transferId: String, name: String, discard: Boolean = false): Boolean {
        return try {
            val discardQuery = if (discard) "&discard=1" else ""
            val url = "${baseUrl()}/api/files/cancel?t=${encode(token)}&id=${encode(transferId)}&name=${encode(name)}$discardQuery"
            val request = Request.Builder().url(url).build()
            client.newCall(request).execute().use { it.isSuccessful }
        } catch (_: Exception) {
            false
        }
    }

    private fun readHttpResponseHead(input: InputStream): HttpResponseHead? {
        val bytes = ByteArrayOutputStream()
        val terminator = byteArrayOf(13, 10, 13, 10)
        var matched = 0
        while (bytes.size() < MAX_RESPONSE_HEADER_BYTES) {
            val value = input.read()
            if (value < 0) return null
            bytes.write(value)
            if (value.toByte() == terminator[matched]) {
                matched++
                if (matched == terminator.size) break
            } else {
                matched = if (value == 13) 1 else 0
            }
        }
        if (matched != terminator.size) return null

        val lines = String(bytes.toByteArray(), StandardCharsets.ISO_8859_1).split("\r\n")
        val statusCode = lines.firstOrNull()?.split(' ')?.getOrNull(1)?.toIntOrNull() ?: return null
        val contentLength = lines.firstOrNull { it.startsWith("Content-Length:", ignoreCase = true) }
            ?.substringAfter(':')?.trim()?.toLongOrNull() ?: -1L
        return HttpResponseHead(statusCode, contentLength)
    }

    /**
     * OkHttp 的 byteStream 内部通常按 8 KB 返回；先在内存中聚合，再交给目标流批量写入，
     * 避免 MediaStore 为每个小段承担一次受控存储写入开销。
     */
    private fun readBatch(input: InputStream, buffer: ByteArray, expected: Int): Int {
        var offset = 0
        while (offset < expected) {
            val read = input.read(buffer, offset, expected - offset)
            if (read < 0) break
            if (read == 0) break
            offset += read
        }
        return if (offset == 0) -1 else offset
    }

    private data class HttpResponseHead(val statusCode: Int, val contentLength: Long)

    companion object {
        private const val TRANSFER_BATCH_BYTES = 512 * 1024
        private const val SOCKET_BUFFER_BYTES = 2 * 1024 * 1024
        private const val HTTP_INPUT_BUFFER_BYTES = 256 * 1024
        private const val CONNECT_TIMEOUT_MILLISECONDS = 4_000
        private const val SOCKET_IDLE_TIMEOUT_MILLISECONDS = 30_000
        private const val MAX_RESPONSE_HEADER_BYTES = 32 * 1024
        private const val PROGRESS_INTERVAL_NANOS = 125_000_000L
        private const val DiscoveryPort = 18121
        private const val DiscoveryRequest = "XTOOL_DISCOVER_V1"
        private val SharedClient = OkHttpClient.Builder()
            .connectTimeout(4, TimeUnit.SECONDS)
            .readTimeout(35, TimeUnit.MINUTES)
            .writeTimeout(35, TimeUnit.MINUTES)
            .build()

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
