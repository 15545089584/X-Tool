package com.xtool.collab

import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.net.Uri
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.compose.setContent
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Surface
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableLongStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.core.content.FileProvider
import com.journeyapps.barcodescanner.ScanContract
import com.journeyapps.barcodescanner.ScanOptions
import com.xtool.collab.data.CollabApi
import com.xtool.collab.data.SessionStore
import com.xtool.collab.ui.XToolCollabTheme
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.io.ByteArrayOutputStream
import java.io.File
import java.util.Base64

class MainActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val session = SessionStore(applicationContext)
        setContent {
            XToolCollabTheme {
                AppRoot(session)
            }
        }
    }
}

private enum class Screen { Pair, Home }

@Composable
private fun AppRoot(session: SessionStore) {
    var screen by remember { mutableStateOf(if (session.token.isNotEmpty() && session.host.isNotEmpty()) Screen.Home else Screen.Pair) }
    var host by remember { mutableStateOf(session.host) }
    var token by remember { mutableStateOf(session.token) }
    var pendingScanPin by remember { mutableStateOf<String?>(null) }
    val scanLauncher = rememberLauncherForActivityResult(ScanContract()) { result ->
        result?.contents?.let { content ->
            // 二维码内容形如 http://192.168.31.214:18120/pair?pin=123456
            parsePairUrl(content)?.let { parsed ->
                host = parsed.host
                token = ""
                pendingScanPin = parsed.pin
            }
        }
    }

    Surface(modifier = Modifier.fillMaxSize(), color = MaterialTheme.colorScheme.background) {
        when (screen) {
            Screen.Pair -> PairScreen(
                initialHost = host,
                initialPin = pendingScanPin,
                onScanRequest = { scanLauncher.launch(scanOptions()) },
                onConnected = { newHost, newToken ->
                    host = newHost
                    token = newToken
                    pendingScanPin = null
                    session.host = newHost
                    session.token = newToken
                    screen = Screen.Home
                }
            )
            Screen.Home -> HomeScreen(
                host = host,
                token = token,
                onDisconnect = {
                    session.clear()
                    screen = Screen.Pair
                }
            )
        }
    }
}

private data class PairUrl(val host: String, val pin: String)

private fun parsePairUrl(content: String): PairUrl? {
    return try {
        val uri = Uri.parse(content)
        val hostPart = "${uri.host}:${if (uri.port > 0) uri.port else 80}"
        val pin = uri.getQueryParameter("pin") ?: return null
        PairUrl(hostPart, pin)
    } catch (_: Exception) {
        null
    }
}

private fun scanOptions() = ScanOptions().apply {
    setDesiredBarcodeFormats(ScanOptions.QR_CODE)
    setPrompt("扫描电脑端协作中心的配对二维码")
    setBeepEnabled(false)
}

@Composable
private fun PairScreen(
    initialHost: String,
    initialPin: String?,
    onScanRequest: () -> Unit,
    onConnected: (String, String) -> Unit
) {
    var host by remember { mutableStateOf(initialHost) }
    var pin by remember { mutableStateOf(initialPin ?: "") }
    var status by remember { mutableStateOf("输入电脑地址与配对 PIN，或扫描电脑端二维码") }
    var loading by remember { mutableStateOf(false) }
    val scope = rememberCoroutineScope()

    fun connect() {
        val target = host.trim()
        val code = pin.trim()
        if (target.isEmpty() || code.isEmpty()) {
            status = "请填写电脑地址与配对 PIN"
            return
        }
        loading = true
        status = "正在配对…"
        scope.launch {
            val result = withContext(Dispatchers.IO) { CollabApi(target).pair(code) }
            loading = false
            if (result == null) {
                status = "配对失败：请确认电脑服务已启动、地址与 PIN 正确、双方在同一网络"
            } else {
                status = "配对成功"
                onConnected(target, result)
            }
        }
    }

    // 扫码返回后自动尝试配对。
    LaunchedEffect(initialPin) {
        if (!initialPin.isNullOrBlank()) {
            pin = initialPin
            if (host.isNotBlank()) {
                connect()
            } else {
                status = "已读取二维码，请补充电脑地址后连接"
            }
        }
    }

    Box(modifier = Modifier.fillMaxSize().padding(24.dp), contentAlignment = Alignment.Center) {
        Card(
            modifier = Modifier.fillMaxWidth(),
            shape = RoundedCornerShape(18.dp),
            colors = CardDefaults.cardColors(containerColor = Color(0xF2FFFFFF)),
            elevation = CardDefaults.cardElevation(defaultElevation = 6.dp)
        ) {
            Column(modifier = Modifier.padding(22.dp)) {
                Text("X-Tool 协作", fontSize = 24.sp, fontWeight = FontWeight.SemiBold, color = Color(0xFF2F4A66))
                Text(
                    "与电脑端协作中心配对，共享剪贴板与文件",
                    fontSize = 13.sp,
                    color = Color(0xFF63809C),
                    modifier = Modifier.padding(top = 6.dp)
                )
                Spacer(Modifier.height(18.dp))
                OutlinedTextField(
                    value = host,
                    onValueChange = { host = it },
                    label = { Text("电脑地址") },
                    placeholder = { Text("192.168.31.214:18120") },
                    singleLine = true,
                    modifier = Modifier.fillMaxWidth()
                )
                Spacer(Modifier.height(10.dp))
                OutlinedTextField(
                    value = pin,
                    onValueChange = { pin = it },
                    label = { Text("配对 PIN") },
                    singleLine = true,
                    modifier = Modifier.fillMaxWidth()
                )
                Spacer(Modifier.height(16.dp))
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.spacedBy(10.dp)
                ) {
                    Button(
                        onClick = { connect() },
                        enabled = !loading,
                        colors = ButtonDefaults.buttonColors(containerColor = Color(0xFF4D7CFE)),
                        modifier = Modifier.weight(1f).height(48.dp)
                    ) {
                        if (loading) {
                            CircularProgressIndicator(modifier = Modifier.width(20.dp).height(20.dp), strokeWidth = 2.dp, color = Color.White)
                        } else {
                            Text("连接", fontSize = 15.sp, fontWeight = FontWeight.SemiBold)
                        }
                    }
                    OutlinedButton(
                        onClick = onScanRequest,
                        modifier = Modifier.weight(1f).height(48.dp)
                    ) { Text("扫码配对", fontSize = 14.sp) }
                }
                Text(status, fontSize = 12.sp, color = Color(0xFF7B93A8), modifier = Modifier.padding(top = 12.dp))
            }
        }
    }
}

@Composable
private fun HomeScreen(
    host: String,
    token: String,
    onDisconnect: () -> Unit
) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    var autoSync by remember { mutableStateOf(true) }
    var latestText by remember { mutableStateOf("") }
    var latestImage by remember { mutableStateOf<Bitmap?>(null) }
    var sendText by remember { mutableStateOf("") }
    var status by remember { mutableStateOf("已连接到 $host") }
    var lastSeq by remember { mutableLongStateOf(0L) }
    val clipboard = context.getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager

    // 电脑 → 手机：轮询拉取并写入手机剪贴板。
    LaunchedEffect(autoSync, token) {
        if (!autoSync || token.isBlank()) return@LaunchedEffect
        while (isActive) {
            val (current, entries) = withContext(Dispatchers.IO) {
                CollabApi(host).pullClipboard(token, lastSeq)
            }
            if (current > lastSeq) {
                lastSeq = current
                entries.lastOrNull()?.let { entry ->
                    if (entry.kind == "text" && entry.text.isNotEmpty()) {
                        latestText = entry.text
                        latestImage = null
                        clipboard.setPrimaryClip(ClipData.newPlainText(ClipLabel, entry.text))
                    } else if (entry.kind == "image" && entry.imageBase64.isNotEmpty()) {
                        val bitmap = decodeImage(entry.imageBase64)
                        if (bitmap != null) {
                            latestImage = bitmap
                            latestText = ""
                            writeImageToClipboard(context, bitmap)
                        }
                    }
                }
            }
            delay(3000)
        }
    }

    // 手机 → 电脑：监听手机剪贴板变化并推送；跳过自己写入的内容避免回环。
    DisposableEffect(autoSync) {
        val listener = ClipboardManager.OnPrimaryClipChangedListener {
            if (!autoSync) return@OnPrimaryClipChangedListener
            val clip = clipboard.primaryClip ?: return@OnPrimaryClipChangedListener
            if (clip.description?.label == ClipLabel) return@OnPrimaryClipChangedListener
            val item = clip.getItemAt(0)
            val text = item.coerceToText(context)?.toString()
            if (!text.isNullOrBlank()) {
                scope.launch {
                    val ok = withContext(Dispatchers.IO) { CollabApi(host).pushClipboardText(token, text) }
                    status = if (ok) "已同步手机剪贴板到电脑" else "同步失败"
                }
            } else if (item.uri != null || clip.description?.hasMimeType("image/*") == true) {
                scope.launch {
                    // 剪贴板图片以 content Uri 携带，从流解码为位图。
                    val bitmap = item.uri?.let { uri ->
                        runCatching {
                            context.contentResolver.openInputStream(uri)?.use { BitmapFactory.decodeStream(it) }
                        }.getOrNull()
                    }
                    if (bitmap != null) {
                        val png = ByteArrayOutputStream().apply {
                            bitmap.compress(Bitmap.CompressFormat.PNG, 100, this)
                        }.toByteArray()
                        val ok = withContext(Dispatchers.IO) { CollabApi(host).pushClipboardImage(token, png) }
                        status = if (ok) "已同步手机图片到电脑" else "图片同步失败"
                    }
                }
            }
        }
        clipboard.addPrimaryClipChangedListener(listener)
        onDispose { clipboard.removePrimaryClipChangedListener(listener) }
    }

    Column(
        modifier = Modifier
            .fillMaxSize()
            .verticalScroll(rememberScrollState())
            .padding(20.dp)
    ) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Text("已连接", fontSize = 20.sp, fontWeight = FontWeight.SemiBold, color = Color(0xFF2F4A66))
            Spacer(Modifier.width(8.dp))
            Box(
                modifier = Modifier
                    .width(9.dp)
                    .height(9.dp)
                    .background(Color(0xFF16B99B), RoundedCornerShape(5.dp))
            )
            Spacer(Modifier.weight(1f))
            OutlinedButton(onClick = onDisconnect) {
                Text("断开", fontSize = 13.sp)
            }
        }
        Text("电脑端：$host", fontSize = 13.sp, color = Color(0xFF63809C), modifier = Modifier.padding(top = 6.dp))

        Card(
            modifier = Modifier.fillMaxWidth().padding(top = 18.dp),
            shape = RoundedCornerShape(16.dp),
            colors = CardDefaults.cardColors(containerColor = Color(0xF2FFFFFF))
        ) {
            Column(modifier = Modifier.padding(18.dp)) {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Text("剪贴板自动同步", fontSize = 16.sp, fontWeight = FontWeight.SemiBold, color = Color(0xFF2F4A66))
                    Spacer(Modifier.weight(1f))
                    Switch(checked = autoSync, onCheckedChange = { autoSync = it })
                }
                Text(
                    if (autoSync) "手机与电脑剪贴板实时同步（需保持本应用在前台；后台自动同步将在无障碍版本接入）" else "已关闭自动同步",
                    fontSize = 12.sp,
                    color = Color(0xFF7B93A8),
                    modifier = Modifier.padding(top = 6.dp)
                )

                Spacer(Modifier.height(14.dp))
                Text("最近收到", fontSize = 12.sp, fontWeight = FontWeight.SemiBold, color = Color(0xFF7B93A8))
                if (latestImage != null) {
                    Image(
                        bitmap = latestImage!!.asImageBitmap(),
                        contentDescription = "电脑同步的图片",
                        modifier = Modifier
                            .fillMaxWidth()
                            .padding(top = 8.dp)
                            .background(Color(0xFFEFF5FB), RoundedCornerShape(10.dp)),
                        contentScale = ContentScale.Fit
                    )
                } else {
                    Text(
                        latestText.ifEmpty { "暂无内容，电脑端复制的内容会显示在这里" },
                        fontSize = 14.sp,
                        color = Color(0xFF405F7C),
                        modifier = Modifier.padding(top = 6.dp)
                    )
                }

                Spacer(Modifier.height(14.dp))
                OutlinedTextField(
                    value = sendText,
                    onValueChange = { sendText = it },
                    label = { Text("发送到电脑剪贴板") },
                    modifier = Modifier.fillMaxWidth()
                )
                Button(
                    onClick = {
                        if (sendText.isBlank()) return@Button
                        scope.launch {
                            val ok = withContext(Dispatchers.IO) { CollabApi(host).pushClipboardText(token, sendText) }
                            status = if (ok) "已发送到电脑剪贴板" else "发送失败"
                            if (ok) sendText = ""
                        }
                    },
                    colors = ButtonDefaults.buttonColors(containerColor = Color(0xFF4D7CFE)),
                    modifier = Modifier.fillMaxWidth().height(46.dp).padding(top = 10.dp)
                ) { Text("发送", fontSize = 14.sp, fontWeight = FontWeight.SemiBold) }

                Text(status, fontSize = 12.sp, color = Color(0xFF7B93A8), modifier = Modifier.padding(top = 10.dp))
            }
        }

        Card(
            modifier = Modifier.fillMaxWidth().padding(top = 14.dp),
            shape = RoundedCornerShape(16.dp),
            colors = CardDefaults.cardColors(containerColor = Color(0xF2FFFFFF))
        ) {
            Column(modifier = Modifier.padding(18.dp)) {
                Text("文件传输", fontSize = 16.sp, fontWeight = FontWeight.SemiBold, color = Color(0xFF2F4A66))
                Text(
                    "上传与下载功能将在后续版本接入，电脑端网页已支持",
                    fontSize = 12.sp,
                    color = Color(0xFF7B93A8),
                    modifier = Modifier.padding(top = 6.dp)
                )
            }
        }
    }
}

private const val ClipLabel = "XTool-collab"

private fun decodeImage(base64: String): Bitmap? {
    return try {
        val bytes = Base64.getDecoder().decode(base64)
        BitmapFactory.decodeByteArray(bytes, 0, bytes.size)
    } catch (_: Exception) {
        null
    }
}

private fun writeImageToClipboard(context: Context, bitmap: Bitmap) {
    try {
        val dir = File(context.cacheDir, "shared")
        dir.mkdirs()
        val file = File(dir, "clip.png")
        file.outputStream().use { bitmap.compress(Bitmap.CompressFormat.PNG, 100, it) }
        val uri: Uri = FileProvider.getUriForFile(context, "com.xtool.collab.fileprovider", file)
        val clip = ClipData.newUri(context.contentResolver, ClipLabel, uri)
        (context.getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager).setPrimaryClip(clip)
    } catch (_: Exception) {
        // 图片写入失败不影响文本同步。
    }
}
