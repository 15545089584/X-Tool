package com.xtool.collab.data

import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody
import okhttp3.ResponseBody
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
                    tailscaleHost = json.optString("tailscaleHost"),
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
        val suffix = if (automatic) "?auto=1" else ""
        val request = Request.Builder().url("${baseUrl()}/api/status$suffix")
            .header("Authorization", "Bearer $token").build()
        StatusClient.newCall(request).execute().use { response ->
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

    fun listRemoteRoots(token: String): List<RemoteFileRoot> {
        val request = authorizedRequest("${baseUrl()}/api/v2/remote-files/roots", token)
        client.newCall(request).execute().use { response ->
            if (!response.isSuccessful) throw RemoteFileApiException(readError(response.body, response.code))
            val array = JSONObject(response.body?.string() ?: error("电脑端响应为空"))
                .optJSONArray("roots") ?: return emptyList()
            return (0 until array.length()).mapNotNull { index ->
                val item = array.optJSONObject(index) ?: return@mapNotNull null
                val entryToken = item.optString("token")
                if (entryToken.isBlank()) return@mapNotNull null
                RemoteFileRoot(item.optString("id"), item.optString("name", "授权目录"), entryToken)
            }
        }
    }

    fun listRemoteEntries(token: String, directoryToken: String, offset: Int = 0, limit: Int = 100): RemoteFilePage {
        val url = "${baseUrl()}/api/v2/remote-files/list?directory=${encode(directoryToken)}&offset=$offset&limit=$limit"
        val request = authorizedRequest(url, token)
        client.newCall(request).execute().use { response ->
            if (!response.isSuccessful) throw RemoteFileApiException(readError(response.body, response.code))
            val json = JSONObject(response.body?.string() ?: error("电脑端响应为空"))
            val array = json.optJSONArray("entries")
            val entries = if (array == null) emptyList() else (0 until array.length()).mapNotNull { index ->
                val item = array.optJSONObject(index) ?: return@mapNotNull null
                val entryToken = item.optString("token")
                if (entryToken.isBlank()) return@mapNotNull null
                RemoteFileEntry(
                    name = item.optString("name", "未命名"),
                    isDirectory = item.optBoolean("isDirectory"),
                    size = item.optLong("size"),
                    modifiedAt = item.optString("modifiedAt"),
                    token = entryToken,
                    extension = item.optString("extension")
                )
            }
            return RemoteFilePage(
                currentName = json.optString("currentName", "授权目录"),
                parentToken = json.optString("parentToken"),
                nextOffset = json.optInt("nextOffset", -1),
                entries = entries
            )
        }
    }

    fun downloadRemoteFile(
        token: String,
        transferId: String,
        file: RemoteFileEntry,
        openOutput: () -> java.io.OutputStream,
        onProgress: (Long, Long) -> Unit
    ): Boolean {
        val url = "${baseUrl()}/api/v2/remote-files/download?file=${encode(file.token)}&id=${encode(transferId)}"
        val request = authorizedRequest(url, token)
        client.newCall(request).execute().use { response ->
            if (!response.isSuccessful) throw RemoteFileApiException(readError(response.body, response.code))
            val body = response.body ?: throw RemoteFileApiException("电脑端未返回文件内容")
            val total = body.contentLength().takeIf { it >= 0 } ?: file.size
            body.byteStream().use { input ->
                openOutput().use { output ->
                    val buffer = ByteArray(TRANSFER_BATCH_BYTES)
                    var downloaded = 0L
                    var lastProgressAt = 0L
                    while (true) {
                        val read = input.read(buffer)
                        if (read < 0) break
                        if (read == 0) continue
                        output.write(buffer, 0, read)
                        downloaded += read
                        val now = System.nanoTime()
                        if (downloaded >= total || now - lastProgressAt >= PROGRESS_INTERVAL_NANOS) {
                            lastProgressAt = now
                            onProgress(downloaded, total)
                        }
                    }
                    output.flush()
                    return total <= 0 || downloaded == total
                }
            }
        }
    }

    fun downloadRemoteFileSegment(
        token: String,
        transferId: String,
        file: RemoteFileEntry,
        offset: Long,
        length: Long,
        openOutput: () -> java.io.OutputStream,
        onProgress: (Long, Long) -> Unit
    ): Boolean {
        require(offset >= 0 && length > 0 && offset + length <= file.size) { "远程文件分段范围无效" }
        val url = "${baseUrl()}/api/v2/remote-files/download?file=${encode(file.token)}&id=${encode(transferId)}&parallel=1"
        val endInclusive = offset + length - 1
        val request = authorizedRequest(url, token).newBuilder()
            .header("Range", "bytes=$offset-$endInclusive")
            .build()
        client.newCall(request).execute().use { response ->
            if (response.code != 206) throw RemoteFileApiException(readError(response.body, response.code))
            val body = response.body ?: throw RemoteFileApiException("电脑端未返回文件分段")
            val responseLength = body.contentLength().takeIf { it >= 0 } ?: length
            if (responseLength != length) throw RemoteFileApiException("电脑端返回的文件分段长度不一致")
            body.byteStream().use { input ->
                openOutput().use { output ->
                    val buffer = ByteArray(TRANSFER_BATCH_BYTES)
                    var downloaded = 0L
                    var lastProgressAt = 0L
                    while (downloaded < length) {
                        val expected = minOf(buffer.size.toLong(), length - downloaded).toInt()
                        val read = input.read(buffer, 0, expected)
                        if (read < 0) break
                        if (read == 0) continue
                        output.write(buffer, 0, read)
                        downloaded += read
                        val now = System.nanoTime()
                        if (downloaded >= length || now - lastProgressAt >= PROGRESS_INTERVAL_NANOS) {
                            lastProgressAt = now
                            onProgress(downloaded, length)
                        }
                    }
                    output.flush()
                    return downloaded == length
                }
            }
        }
    }

    private fun authorizedRequest(url: String, token: String): Request = Request.Builder()
        .url(url)
        .header("Authorization", "Bearer $token")
        .build()

    private fun readError(body: ResponseBody?, status: Int): String = try {
        JSONObject(body?.string().orEmpty()).optString("error").ifBlank { "电脑端返回错误（$status）" }
    } catch (_: Exception) {
        "电脑端返回错误（$status）"
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
        // 状态探测不能继承大文件传输的 35 分钟读取超时，否则旧热点地址会卡住重连。
        private val StatusClient = SharedClient.newBuilder()
            .connectTimeout(2, TimeUnit.SECONDS)
            .readTimeout(4, TimeUnit.SECONDS)
            .callTimeout(5, TimeUnit.SECONDS)
            .build()

        fun hasLocalDiscoveryNetwork(): Boolean = LanDiscovery.bindings().isNotEmpty()

        /** 通过局域网广播查找电脑，不携带任何配对令牌。 */
        fun discover(timeoutMs: Int = 1800): List<DiscoveredServer> {
            val found = linkedMapOf<String, DiscoveredServer>()
            val sockets = mutableListOf<Pair<DatagramSocket, LanDiscovery.Binding>>()
            try {
                val requestBytes = DiscoveryRequest.toByteArray(Charsets.UTF_8)
                for (binding in LanDiscovery.bindings()) {
                    var socket: DatagramSocket? = null
                    try {
                        socket = DatagramSocket(InetSocketAddress(binding.address, 0))
                        socket.broadcast = true
                        socket.soTimeout = 80
                        socket.send(DatagramPacket(requestBytes, requestBytes.size, binding.broadcast, DiscoveryPort))
                        sockets += socket to binding
                    } catch (_: java.io.IOException) { socket?.close() }
                }
                // 全部接口共享总预算，不为每块网卡分别等待一轮；不扫描子网主机。
                val deadline = System.nanoTime() + timeoutMs.coerceIn(100, 5000) * 1_000_000L
                val responseBuffer = ByteArray(2048)
                while (sockets.isNotEmpty() && System.nanoTime() < deadline) {
                    for ((socket, binding) in sockets) {
                        if (System.nanoTime() >= deadline) break
                        try {
                            val response = DatagramPacket(responseBuffer, responseBuffer.size)
                            socket.receive(response)
                            if (!LanDiscovery.sameSubnet(binding, response.address)) continue
                            val json = JSONObject(String(response.data, response.offset, response.length, Charsets.UTF_8))
                            if (json.optString("service") != "xtool-collaboration-v1") continue
                            val discoveredHost = LanDiscovery.responseHost(response.address, json.optString("host")) ?: continue
                            val serverId = json.optString("serverId")
                            if (discoveredHost.isBlank() || serverId.isBlank()) continue
                            found[serverId] = DiscoveredServer(
                                host = discoveredHost,
                                tailscaleHost = json.optString("tailscaleHost"),
                                serverId = serverId,
                                serverName = json.optString("serverName", "X-Tool 电脑"),
                                autoReconnectAllowed = json.optBoolean("autoReconnectAllowed", true)
                            )
                        } catch (_: java.net.SocketTimeoutException) {
                            // 在总等待时间内继续接收其它网卡或电脑的响应。
                        } catch (_: Exception) {
                            // 无效发现报文不打断其他接口。
                        }
                    }
                }
            } finally { sockets.forEach { (socket, _) -> socket.close() } }
            return found.values.toList()
        }

        private fun encode(value: String) = URLEncoder.encode(value, "UTF-8")
    }
}

data class PairResult(
    val token: String? = null,
    val host: String = "",
    val tailscaleHost: String = "",
    val serverId: String = "",
    val serverName: String = "",
    val error: String? = null
)

data class RemoteFile(val id: String, val name: String, val size: Long, val at: String)

data class RemoteFileRoot(val id: String, val name: String, val token: String)

data class RemoteFileEntry(
    val name: String,
    val isDirectory: Boolean,
    val size: Long,
    val modifiedAt: String,
    val token: String,
    val extension: String
)

data class RemoteFilePage(
    val currentName: String,
    val parentToken: String,
    val nextOffset: Int,
    val entries: List<RemoteFileEntry>
)

class RemoteFileApiException(message: String) : java.io.IOException(message)

data class DiscoveredServer(
    val host: String,
    val tailscaleHost: String,
    val serverId: String,
    val serverName: String,
    val autoReconnectAllowed: Boolean
)
