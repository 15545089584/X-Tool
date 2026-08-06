package com.xtool.collab.data

import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import org.json.JSONObject
import java.util.concurrent.TimeUnit

/** 与电脑端协作中心 HTTP 服务对接的轻量客户端；所有调用均在 IO 线程执行。 */
class CollabApi(private val host: String) {
    private val client = OkHttpClient.Builder()
        .connectTimeout(5, TimeUnit.SECONDS)
        .readTimeout(20, TimeUnit.SECONDS)
        .build()

    private val jsonMedia = "application/json; charset=utf-8".toMediaType()
    private val pngMedia = "image/png".toMediaType()

    private fun baseUrl() = "http://$host"

    /** 用 PIN 配对；成功返回令牌，失败返回可展示的原因。 */
    fun pair(pin: String): PairResult {
        return try {
            val request = Request.Builder().url("${baseUrl()}/api/pair?pin=$pin").build()
            client.newCall(request).execute().use { response ->
                if (!response.isSuccessful) {
                    return PairResult(
                        token = null,
                        error = if (response.code == 401) "配对 PIN 不正确" else "电脑端返回错误（${response.code}）"
                    )
                }
                val json = JSONObject(response.body?.string() ?: return PairResult(null, "电脑端响应为空"))
                val token = json.optString("token").takeIf { it.isNotEmpty() }
                PairResult(token, if (token == null) "电脑端未返回会话令牌" else null)
            }
        } catch (_: java.io.IOException) {
            PairResult(null, "无法连接电脑：请检查地址、双方网络与防火墙")
        } catch (_: Exception) {
            PairResult(null, "配对请求异常，请重试")
        }
    }

    /** 查询电脑端状态：服务是否运行、当前剪贴板序号与最新条目。 */
    fun status(token: String): JSONObject? {
        val request = Request.Builder().url("${baseUrl()}/api/status?t=$token").build()
        client.newCall(request).execute().use { response ->
            if (!response.isSuccessful) return null
            return JSONObject(response.body?.string() ?: return null)
        }
    }

    /** 把手机文本推送到电脑剪贴板。 */
    fun pushClipboardText(token: String, text: String): Boolean {
        val body = JSONObject().put("text", text).toString().toRequestBody(jsonMedia)
        val request = Request.Builder().url("${baseUrl()}/api/clipboard/push?t=$token")
            .post(body)
            .build()
        client.newCall(request).execute().use { return it.isSuccessful }
    }

    /** 把手机图片（PNG 字节）推送到电脑剪贴板。 */
    fun pushClipboardImage(token: String, pngBytes: ByteArray): Boolean {
        val request = Request.Builder().url("${baseUrl()}/api/clipboard/push?t=$token")
            .post(pngBytes.toRequestBody(pngMedia))
            .build()
        client.newCall(request).execute().use { return it.isSuccessful }
    }

    /** 拉取自 since 之后的新剪贴板条目，返回 (最新序号, 条目列表)。 */
    fun pullClipboard(token: String, since: Long): Pair<Long, List<ClipboardEntry>> {
        val request = Request.Builder().url("${baseUrl()}/api/clipboard/pull?t=$token&since=$since").build()
        client.newCall(request).execute().use { response ->
            if (!response.isSuccessful) return 0L to emptyList()
            val json = JSONObject(response.body?.string() ?: return 0L to emptyList())
            val current = json.optLong("currentSeq", 0)
            val entries = json.optJSONArray("entries")?.let { array ->
                (0 until array.length()).mapNotNull { index ->
                    val item = array.optJSONObject(index) ?: return@mapNotNull null
                    ClipboardEntry(
                        seq = item.optLong("seq"),
                        kind = item.optString("kind"),
                        text = item.optString("text"),
                        imageBase64 = item.optString("image")
                    )
                }
            } ?: emptyList()
            return current to entries
        }
    }

    /** 上传文件到电脑接收目录。 */
    fun uploadFile(token: String, name: String, bytes: ByteArray): Boolean {
        val request = Request.Builder()
            .url("${baseUrl()}/api/files/upload?t=$token&name=${java.net.URLEncoder.encode(name, "UTF-8")}")
            .put(bytes.toRequestBody("application/octet-stream".toMediaType()))
            .build()
        client.newCall(request).execute().use { return it.isSuccessful }
    }

    /** 读取电脑发送目录文件列表（JSON 字符串，解析后展示）。 */
    fun listOutgoingFiles(token: String): String {
        val request = Request.Builder().url("${baseUrl()}/api/files/list?t=$token&dir=outgoing").build()
        client.newCall(request).execute().use { return it.body?.string() ?: "{}" }
    }
}

data class ClipboardEntry(
    val seq: Long,
    val kind: String,
    val text: String,
    val imageBase64: String
)

data class PairResult(val token: String?, val error: String?)
