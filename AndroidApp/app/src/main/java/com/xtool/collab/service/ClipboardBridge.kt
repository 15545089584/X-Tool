package com.xtool.collab.service

import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.os.Handler
import android.os.Looper
import android.util.Log
import androidx.core.content.FileProvider
import com.xtool.collab.data.CollabApi
import java.io.ByteArrayOutputStream
import java.io.File
import java.util.Base64

/** 剪贴板桥接公共逻辑：手机侧读写剪贴板、与电脑端双向同步。 */
object ClipboardBridge {
    const val Label = "XTool-collab"

    /** 电脑 → 手机同步结果的界面状态，主线程回调。 */
    object SyncState {
        var lastText: String = ""
        var lastImage: Bitmap? = null
        var status: String = ""
        private val listeners = mutableListOf<() -> Unit>()
        private val handler = Handler(Looper.getMainLooper())

        fun onChange(action: () -> Unit): () -> Unit {
            listeners.add(action)
            return { listeners.remove(action) }
        }

        fun update(block: SyncState.() -> Unit) {
            handler.post {
                block()
                listeners.toList().forEach { it() }
            }
        }
    }

    fun decodeImage(base64: String): Bitmap? = try {
        val bytes = Base64.getDecoder().decode(base64)
        BitmapFactory.decodeByteArray(bytes, 0, bytes.size)
    } catch (_: Exception) {
        null
    }

    fun clipboard(context: Context) = context.getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager

    /** 把位图以 content Uri 写入手机剪贴板，其它应用可直接粘贴。 */
    fun writeImageToClipboard(context: Context, bitmap: Bitmap) {
        try {
            val dir = File(context.cacheDir, "shared")
            dir.mkdirs()
            val file = File(dir, "clip.png")
            file.outputStream().use { bitmap.compress(Bitmap.CompressFormat.PNG, 100, it) }
            val uri = FileProvider.getUriForFile(context, "com.xtool.collab.fileprovider", file)
            clipboard(context).setPrimaryClip(ClipData.newUri(context.contentResolver, Label, uri))
        } catch (_: Exception) {
            // 图片写入失败不影响文本同步。
        }
    }

    /** 读取手机剪贴板并推送到电脑；跳过自身写入的条目避免回环。 */
    fun pushFromClipboard(context: Context, host: String, token: String) {
        val clip = clipboard(context).primaryClip ?: return
        Log.d(LogTag, "剪贴板变化：label=${clip.description?.label}")
        if (clip.description?.label == Label) return
        val item = clip.getItemAt(0)
        val text = item.coerceToText(context)?.toString()
        val signature = text ?: item.uri?.toString() ?: return
        Log.d(LogTag, "读取内容：文本长度=${text?.length ?: -1}，uri=${item.uri}")
        // 多监听器（无障碍服务 + 前台服务）并发触发时去重，避免重复推送。
        val now = System.currentTimeMillis()
        val previous = lastPush[signature]
        if (previous != null && now - previous < 5000) return
        lastPush[signature] = now
        if (!text.isNullOrBlank()) {
            Thread {
                val ok = runCatching { CollabApi(host).pushClipboardText(token, text) }.getOrDefault(false)
                Log.d(LogTag, "推送文本结果：$ok（host=$host）")
                if (ok) SyncState.update { status = "已同步手机剪贴板到电脑" }
            }.start()
        } else {
            val uri = item.uri
            if (uri != null) {
                Thread {
                    val bitmap = runCatching {
                        context.contentResolver.openInputStream(uri)?.use { BitmapFactory.decodeStream(it) }
                    }.getOrNull()
                    if (bitmap != null) {
                        val png = ByteArrayOutputStream().apply {
                            bitmap.compress(Bitmap.CompressFormat.PNG, 100, this)
                        }.toByteArray()
                        val ok = runCatching { CollabApi(host).pushClipboardImage(token, png) }.getOrDefault(false)
                        Log.d(LogTag, "推送图片结果：$ok")
                        if (ok) SyncState.update { status = "已同步手机图片到电脑" }
                    }
                }.start()
            }
        }
    }

    /** 拉取电脑端新条目并写入手机剪贴板；返回电脑端最新序号。 */
    fun pullAndWrite(context: Context, host: String, token: String, lastSeq: Long): Long {
        val (current, entries) = CollabApi(host).pullClipboard(token, lastSeq)
        Log.d(LogTag, "拉取结果：current=$current entries=${entries.size} lastSeq=$lastSeq")
        if (current > lastSeq) {
            entries.lastOrNull()?.let { entry ->
                if (entry.kind == "text" && entry.text.isNotEmpty()) {
                    clipboard(context).setPrimaryClip(ClipData.newPlainText(Label, entry.text))
                    SyncState.update {
                        lastText = entry.text
                        lastImage = null
                        status = "已同步电脑剪贴板到手机"
                    }
                } else if (entry.kind == "image" && entry.imageBase64.isNotEmpty()) {
                    val bitmap = decodeImage(entry.imageBase64)
                    if (bitmap != null) {
                        writeImageToClipboard(context, bitmap)
                        SyncState.update {
                            lastImage = bitmap
                            lastText = ""
                            status = "已同步电脑图片到手机"
                        }
                    }
                }
            }
        }
        return current
    }

    private val lastPush = java.util.concurrent.ConcurrentHashMap<String, Long>()

    private const val LogTag = "XToolCollab"
}
