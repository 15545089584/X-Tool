package com.xtool.collab

import android.Manifest
import android.content.ContentValues
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.graphics.Bitmap
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.os.PowerManager
import android.provider.MediaStore
import android.provider.OpenableColumns
import android.provider.Settings
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
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalLifecycleOwner
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.journeyapps.barcodescanner.ScanContract
import com.journeyapps.barcodescanner.ScanOptions
import com.xtool.collab.data.CollabApi
import com.xtool.collab.data.RemoteFile
import com.xtool.collab.data.SessionStore
import com.xtool.collab.service.ClipboardAccessibilityService
import com.xtool.collab.service.ClipboardBridge
import com.xtool.collab.service.SyncForegroundService
import com.xtool.collab.ui.XToolCollabTheme
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.LifecycleEventObserver
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.io.File

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
    val appContext = LocalContext.current
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
                session = session,
                host = host,
                token = token,
                onDisconnect = {
                    SyncForegroundService.stop(appContext)
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
    setCaptureActivity(PortraitScanActivity::class.java)
    setBeepEnabled(false)
}

@Composable
private fun PairScreen(
    initialHost: String,
    initialPin: String?,
    onScanRequest: () -> Unit,
    onConnected: (String, String) -> Unit
) {
    val context = LocalContext.current
    var host by remember(initialHost) { mutableStateOf(initialHost) }
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
            val result = withContext(Dispatchers.IO) { CollabApi(target).pair(code, SessionStore(context).deviceId) }
            loading = false
            if (result.token != null) {
                status = "配对成功"
                onConnected(target, result.token)
            } else {
                status = result.error ?: "配对失败，请重试"
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
    session: SessionStore,
    host: String,
    token: String,
    onDisconnect: () -> Unit
) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    var autoSync by remember { mutableStateOf(session.syncEnabled) }
    var sendText by remember { mutableStateOf("") }
    var localStatus by remember { mutableStateOf("已连接到 $host") }
    var receivedText by remember { mutableStateOf(ClipboardBridge.SyncState.lastText) }
    var receivedImage by remember { mutableStateOf(ClipboardBridge.SyncState.lastImage) }
    var syncStatus by remember { mutableStateOf(ClipboardBridge.SyncState.status) }
    var accessibilityOn by remember { mutableStateOf(isAccessibilityEnabled(context)) }
    var batteryIgnored by remember { mutableStateOf(isBatteryOptimizationIgnored(context)) }
    var remoteFiles by remember { mutableStateOf<List<RemoteFile>>(emptyList()) }
    var fileStatus by remember { mutableStateOf("") }
    var uploadingName by remember { mutableStateOf<String?>(null) }
    var uploadPercent by remember { mutableStateOf(0) }
    val mainHandler = remember { Handler(Looper.getMainLooper()) }

    // 从系统设置返回后刷新无障碍与电池优化状态，避免提示残留。
    val lifecycleOwner = LocalLifecycleOwner.current
    DisposableEffect(lifecycleOwner) {
        val observer = LifecycleEventObserver { _, event ->
            if (event == Lifecycle.Event.ON_RESUME) {
                accessibilityOn = isAccessibilityEnabled(context)
                batteryIgnored = isBatteryOptimizationIgnored(context)
                // 兜底：系统限制后台读剪贴板时，回到前台立即补推后台期间复制的内容。
                if (token.isNotBlank()) {
                    ClipboardBridge.pushFromClipboard(context, host, token)
                }
            }
        }
        lifecycleOwner.lifecycle.addObserver(observer)
        onDispose { lifecycleOwner.lifecycle.removeObserver(observer) }
    }

    // 订阅电脑 → 手机同步状态，用于界面展示。
    DisposableEffect(Unit) {
        val unsubscribe = ClipboardBridge.SyncState.onChange {
            receivedText = ClipboardBridge.SyncState.lastText
            receivedImage = ClipboardBridge.SyncState.lastImage
            syncStatus = ClipboardBridge.SyncState.status
        }
        onDispose { unsubscribe() }
    }

    // 同步开关控制前台服务启停。
    DisposableEffect(autoSync) {
        session.syncEnabled = autoSync
        if (autoSync && token.isNotBlank()) {
            SyncForegroundService.start(context, host, token)
        } else {
            SyncForegroundService.stop(context)
        }
        onDispose { }
    }

    // Android 13+ 请求通知权限（前台服务通知需要）。
    val notificationPermission = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) { }
    LaunchedEffect(Unit) {
        if (Build.VERSION.SDK_INT >= 33 &&
            context.checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED
        ) {
            notificationPermission.launch(Manifest.permission.POST_NOTIFICATIONS)
        }
    }

    suspend fun refreshFiles() {
        remoteFiles = withContext(Dispatchers.IO) { CollabApi(host).listOutgoingFiles(token) }
    }

    val filePicker = rememberLauncherForActivityResult(ActivityResultContracts.OpenMultipleDocuments()) { uris ->
        scope.launch {
            for (uri in uris) {
                val name = queryDisplayName(context, uri)
                uploadingName = name
                uploadPercent = 0
                // 先落缓存文件保证 Content-Length 完整，再流式上传并回报进度。
                val ok = withContext(Dispatchers.IO) {
                    val cacheFile = File(context.cacheDir, "upload_${System.currentTimeMillis()}_$name")
                    try {
                        context.contentResolver.openInputStream(uri)?.use { input ->
                            cacheFile.outputStream().use { input.copyTo(it) }
                        }
                        val total = cacheFile.length()
                        val result = CollabApi(host).uploadFileStreaming(
                            token,
                            name,
                            { cacheFile.inputStream() },
                            total
                        ) { done, all ->
                            mainHandler.post { uploadPercent = if (all > 0) (done * 100 / all).toInt() else 0 }
                        }
                        result
                    } catch (_: Exception) {
                        false
                    } finally {
                        cacheFile.delete()
                    }
                }
                uploadingName = null
                fileStatus = if (ok) "已上传 $name 到电脑" else "上传失败 $name"
            }
            refreshFiles()
        }
    }

    LaunchedEffect(token) {
        refreshFiles()
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
                    if (autoSync) "后台常驻同步中：电脑内容自动进入手机剪贴板，手机复制自动推送电脑" else "已关闭自动同步",
                    fontSize = 12.sp,
                    color = Color(0xFF7B93A8),
                    modifier = Modifier.padding(top = 6.dp)
                )

                if (!accessibilityOn) {
                    Card(
                        modifier = Modifier.fillMaxWidth().padding(top = 12.dp),
                        shape = RoundedCornerShape(12.dp),
                        colors = CardDefaults.cardColors(containerColor = Color(0xFFFFF6E5))
                    ) {
                        Column(modifier = Modifier.padding(12.dp)) {
                            Text(
                                "未开启无障碍服务：App 退到后台后，手机复制的内容将无法自动推送到电脑。",
                                fontSize = 12.sp,
                                color = Color(0xFF8A5B12)
                            )
                            OutlinedButton(
                                onClick = {
                                    context.startActivity(Intent(Settings.ACTION_ACCESSIBILITY_SETTINGS))
                                },
                                modifier = Modifier.padding(top = 8.dp)
                            ) { Text("去开启无障碍服务", fontSize = 12.sp) }
                        }
                    }
                }

                if (!batteryIgnored) {
                    Card(
                        modifier = Modifier.fillMaxWidth().padding(top = 10.dp),
                        shape = RoundedCornerShape(12.dp),
                        colors = CardDefaults.cardColors(containerColor = Color(0xFFFFF6E5))
                    ) {
                        Column(modifier = Modifier.padding(12.dp)) {
                            Text(
                                "建议允许后台运行：系统省电策略可能冻结同步服务，导致后台同步中断。",
                                fontSize = 12.sp,
                                color = Color(0xFF8A5B12)
                            )
                            Row(modifier = Modifier.padding(top = 8.dp)) {
                                OutlinedButton(
                                    onClick = {
                                        runCatching {
                                            context.startActivity(
                                                Intent(
                                                    Settings.ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS,
                                                    Uri.parse("package:${context.packageName}")
                                                )
                                            )
                                        }
                                    },
                                    modifier = Modifier.weight(1f)
                                ) { Text("允许后台运行", fontSize = 12.sp) }
                                Spacer(Modifier.width(8.dp))
                                OutlinedButton(
                                    onClick = {
                                        runCatching {
                                            context.startActivity(
                                                Intent(
                                                    Settings.ACTION_APPLICATION_DETAILS_SETTINGS,
                                                    Uri.parse("package:${context.packageName}")
                                                )
                                            )
                                        }
                                    },
                                    modifier = Modifier.weight(1f)
                                ) { Text("应用信息", fontSize = 12.sp) }
                            }
                        }
                    }
                }

                Spacer(Modifier.height(14.dp))
                Text("最近收到", fontSize = 12.sp, fontWeight = FontWeight.SemiBold, color = Color(0xFF7B93A8))
                if (receivedImage != null) {
                    Image(
                        bitmap = receivedImage!!.asImageBitmap(),
                        contentDescription = "电脑同步的图片",
                        modifier = Modifier
                            .fillMaxWidth()
                            .padding(top = 8.dp)
                            .background(Color(0xFFEFF5FB), RoundedCornerShape(10.dp)),
                        contentScale = ContentScale.Fit
                    )
                    Row(modifier = Modifier.fillMaxWidth().padding(top = 8.dp)) {
                        OutlinedButton(
                            onClick = {
                                ClipboardBridge.writeImageToClipboard(context, receivedImage!!)
                                localStatus = "图片已复制到手机剪贴板"
                            },
                            modifier = Modifier.weight(1f)
                        ) { Text("复制到剪贴板", fontSize = 12.sp) }
                        Spacer(Modifier.width(8.dp))
                        OutlinedButton(
                            onClick = {
                                val ok = saveImageToGallery(context, receivedImage!!)
                                localStatus = if (ok) "已保存到相册（Pictures/XTool）" else "保存失败：可能需要存储权限"
                            },
                            modifier = Modifier.weight(1f)
                        ) { Text("保存到相册", fontSize = 12.sp) }
                    }
                } else {
                    Text(
                        receivedText.ifEmpty { "暂无内容，电脑端复制的内容会显示在这里" },
                        fontSize = 14.sp,
                        color = Color(0xFF405F7C),
                        modifier = Modifier.padding(top = 6.dp)
                    )
                }
                Text(
                    syncStatus.ifEmpty { "等待同步…" },
                    fontSize = 12.sp,
                    color = Color(0xFF7B93A8),
                    modifier = Modifier.padding(top = 8.dp)
                )

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
                            localStatus = if (ok) "已发送到电脑剪贴板" else "发送失败"
                            if (ok) sendText = ""
                        }
                    },
                    colors = ButtonDefaults.buttonColors(containerColor = Color(0xFF4D7CFE)),
                    modifier = Modifier.fillMaxWidth().height(46.dp).padding(top = 10.dp)
                ) { Text("发送", fontSize = 14.sp, fontWeight = FontWeight.SemiBold) }

                Text(localStatus, fontSize = 12.sp, color = Color(0xFF7B93A8), modifier = Modifier.padding(top = 10.dp))
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
                    "上传到电脑接收目录；电脑端发送的文件可下载到手机“下载/XTool”目录。",
                    fontSize = 12.sp,
                    color = Color(0xFF7B93A8),
                    modifier = Modifier.padding(top = 6.dp)
                )
                Spacer(Modifier.height(12.dp))
                Button(
                    onClick = { filePicker.launch(arrayOf("image/*", "video/*", "audio/*", "application/*", "text/*", "*/*")) },
                    colors = ButtonDefaults.buttonColors(containerColor = Color(0xFF4D7CFE)),
                    modifier = Modifier.fillMaxWidth().height(46.dp)
                ) { Text("选择文件上传到电脑", fontSize = 14.sp, fontWeight = FontWeight.SemiBold) }
                if (uploadingName != null) {
                    Text(
                        "上传中 $uploadingName · $uploadPercent%",
                        fontSize = 12.sp,
                        color = Color(0xFF2F6BC4),
                        modifier = Modifier.padding(top = 8.dp)
                    )
                }
                Text(
                    fileStatus.ifEmpty { "手机上传的文件会保存到电脑的接收目录" },
                    fontSize = 12.sp,
                    color = Color(0xFF7B93A8),
                    modifier = Modifier.padding(top = 8.dp)
                )

                Spacer(Modifier.height(14.dp))
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Text("电脑发送的文件", fontSize = 13.sp, fontWeight = FontWeight.SemiBold, color = Color(0xFF2F4A66))
                    Spacer(Modifier.weight(1f))
                    OutlinedButton(
                        onClick = { scope.launch { refreshFiles() } },
                        modifier = Modifier.height(34.dp)
                    ) { Text("刷新", fontSize = 12.sp) }
                }
                if (remoteFiles.isEmpty()) {
                    Text(
                        "暂无文件，可在电脑端协作中心点“发送文件到手机…”",
                        fontSize = 12.sp,
                        color = Color(0xFF7B93A8),
                        modifier = Modifier.padding(top = 6.dp)
                    )
                } else {
                    remoteFiles.forEach { file ->
                        Row(
                            verticalAlignment = Alignment.CenterVertically,
                            modifier = Modifier.fillMaxWidth().padding(top = 8.dp)
                        ) {
                            Column(modifier = Modifier.weight(1f)) {
                                Text(file.name, fontSize = 13.sp, color = Color(0xFF405F7C), maxLines = 1)
                                Text(formatFileSize(file.size), fontSize = 11.sp, color = Color(0xFF7B93A8))
                            }
                            OutlinedButton(
                                onClick = {
                                    scope.launch {
                                        fileStatus = "正在下载 ${file.name}…"
                                        val ok = withContext(Dispatchers.IO) {
                                            val target = saveToDownloads(context, file.name)
                                            if (target == null) false
                                            else CollabApi(host).downloadFileStreaming(token, file.name) { target }
                                        }
                                        fileStatus = if (ok) "已保存到手机下载/XTool" else "下载失败 ${file.name}"
                                    }
                                },
                                modifier = Modifier.height(34.dp)
                            ) { Text("下载", fontSize = 12.sp) }
                        }
                    }
                }
            }
        }
    }
}

private fun isAccessibilityEnabled(context: Context): Boolean {
    val expected = "${context.packageName}/${ClipboardAccessibilityService::class.java.name}"
    val enabled = Settings.Secure.getString(context.contentResolver, Settings.Secure.ENABLED_ACCESSIBILITY_SERVICES)
        ?: return false
    return enabled.split(':').any { it.equals(expected, ignoreCase = true) }
}

private fun isBatteryOptimizationIgnored(context: Context): Boolean {
    val powerManager = context.getSystemService(Context.POWER_SERVICE) as PowerManager
    return powerManager.isIgnoringBatteryOptimizations(context.packageName)
}

private fun saveImageToGallery(context: Context, bitmap: Bitmap): Boolean {
    return try {
        val values = ContentValues().apply {
            put(MediaStore.Images.Media.DISPLAY_NAME, "xtool_${System.currentTimeMillis()}.png")
            put(MediaStore.Images.Media.MIME_TYPE, "image/png")
            if (Build.VERSION.SDK_INT >= 29) {
                put(MediaStore.Images.Media.RELATIVE_PATH, "Pictures/XTool")
            }
        }
        val uri = context.contentResolver.insert(MediaStore.Images.Media.EXTERNAL_CONTENT_URI, values) ?: return false
        context.contentResolver.openOutputStream(uri)?.use { out ->
            bitmap.compress(Bitmap.CompressFormat.PNG, 100, out)
        } ?: return false
        true
    } catch (_: Exception) {
        false
    }
}

private fun queryDisplayName(context: Context, uri: Uri): String {
    context.contentResolver.query(uri, null, null, null, null)?.use { cursor ->
        val index = cursor.getColumnIndex(OpenableColumns.DISPLAY_NAME)
        if (index >= 0 && cursor.moveToFirst()) {
            val name = cursor.getString(index)
            if (!name.isNullOrBlank()) return name
        }
    }
    return "file_${System.currentTimeMillis()}"
}

private fun saveToDownloads(context: Context, name: String): java.io.OutputStream? {
    return try {
        val values = ContentValues().apply {
            put(MediaStore.Downloads.DISPLAY_NAME, name)
            put(MediaStore.Downloads.MIME_TYPE, "application/octet-stream")
            if (Build.VERSION.SDK_INT >= 29) {
                put(MediaStore.Downloads.RELATIVE_PATH, "Download/XTool")
            }
        }
        val uri = context.contentResolver.insert(MediaStore.Downloads.EXTERNAL_CONTENT_URI, values) ?: return null
        context.contentResolver.openOutputStream(uri)
    } catch (_: Exception) {
        null
    }
}

private fun formatFileSize(bytes: Long): String {
    if (bytes >= 1024L * 1024 * 1024) return String.format("%.1f GB", bytes / 1024.0 / 1024 / 1024)
    if (bytes >= 1024L * 1024) return String.format("%.1f MB", bytes / 1024.0 / 1024)
    if (bytes >= 1024) return String.format("%.1f KB", bytes / 1024.0)
    return "$bytes B"
}
