package com.xtool.collab

import android.Manifest
import android.app.NotificationChannel
import android.app.NotificationManager
import android.content.ContentResolver
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.database.Cursor
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.provider.OpenableColumns
import androidx.activity.ComponentActivity
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.compose.setContent
import androidx.activity.result.contract.ActivityResultContracts
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
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.core.app.NotificationCompat
import androidx.core.content.ContextCompat
import com.journeyapps.barcodescanner.ScanOptions
import com.journeyapps.barcodescanner.ScanContract
import com.xtool.collab.data.CollabApi
import com.xtool.collab.data.MobileTransfer
import com.xtool.collab.data.SessionStore
import com.xtool.collab.data.TransferRuntime
import com.xtool.collab.service.SyncForegroundService
import com.xtool.collab.ui.XToolCollabTheme
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.util.UUID

class MainActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        requestNotificationPermission()
        val session = SessionStore(applicationContext)
        if (session.autoConnectEnabled && session.token.isNotBlank()) {
            runCatching { SyncForegroundService.start(this, automatic = true) }
        }
        setContent {
            XToolCollabTheme {
                CollaborationApp(session)
            }
        }
    }

    private fun requestNotificationPermission() {
        if (Build.VERSION.SDK_INT >= 33 &&
            ContextCompat.checkSelfPermission(this, Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED) {
            requestPermissions(arrayOf(Manifest.permission.POST_NOTIFICATIONS), 100)
        }
    }
}

private val Blue = Color(0xFF4D7CFE)
private val Ink = Color(0xFF29445F)
private val Muted = Color(0xFF72889D)
private val Mint = Color(0xFF20B58B)
private val SoftBackground = Color(0xFFF0F6FB)

@Composable
private fun CollaborationApp(session: SessionStore) {
    var paired by remember { mutableStateOf(session.token.isNotBlank()) }
    val background = Brush.verticalGradient(listOf(Color(0xFFEAF4FF), Color(0xFFF7FBFE), Color(0xFFEAFBF7)))
    Box(Modifier.fillMaxSize().background(background)) {
        if (paired) {
            HomeScreen(session, onDisconnected = { paired = false })
        } else {
            PairScreen(session, onPaired = { paired = true })
        }
    }
}

@Composable
private fun PairScreen(session: SessionStore, onPaired: () -> Unit) {
    val context = androidx.compose.ui.platform.LocalContext.current
    val scope = rememberCoroutineScope()
    var host by remember { mutableStateOf(session.host.ifBlank { "192.168.1.2:18120" }) }
    var pin by remember { mutableStateOf(session.pin) }
    var message by remember { mutableStateOf("请在电脑协作中心扫描配对码") }
    var connecting by remember { mutableStateOf(false) }

    fun applyScan(text: String) {
        parsePairUrl(text)?.let {
            host = it.first
            pin = it.second
            message = "已读取电脑地址，正在配对"
        } ?: run { message = "未识别为 X-Tool 配对码" }
    }

    fun connect() {
        val normalizedHost = normalizeHost(host)
        if (normalizedHost.isBlank() || pin.length != 6) {
            message = "请输入电脑地址和 6 位配对码"
            return
        }
        connecting = true
        scope.launch {
            val result = withContext(Dispatchers.IO) {
                CollabApi(normalizedHost).pair(pin, session.deviceId, "${Build.MANUFACTURER} ${Build.MODEL}")
            }
            connecting = false
            if (result.token == null) {
                message = result.error ?: "配对失败"
                return@launch
            }
            session.host = result.host.ifBlank { normalizedHost }
            session.token = result.token
            session.pin = pin
            session.serverId = result.serverId
            session.serverName = result.serverName
            SyncForegroundService.start(context, automatic = false)
            onPaired()
        }
    }

    val scanner = rememberLauncherForActivityResult(ScanContract()) { result ->
        if (!result?.contents.isNullOrBlank()) {
            applyScan(result.contents)
            connect()
        }
    }

    Column(
        Modifier.fillMaxSize().padding(horizontal = 24.dp, vertical = 34.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.Center
    ) {
        Box(
            Modifier.size(82.dp).background(Brush.linearGradient(listOf(Blue, Color(0xFF6B9BFF))), RoundedCornerShape(26.dp)),
            contentAlignment = Alignment.Center
        ) { Text("↔", color = Color.White, fontSize = 38.sp, fontWeight = FontWeight.Bold) }
        Spacer(Modifier.height(22.dp))
        Text("连接你的 X-Tool", fontSize = 27.sp, fontWeight = FontWeight.Bold, color = Ink)
        Text("首次扫码建立信任，之后可在同一局域网自动连接", Modifier.padding(top = 8.dp), color = Muted, fontSize = 13.sp)
        Spacer(Modifier.height(26.dp))
        Card(
            colors = CardDefaults.cardColors(containerColor = Color.White.copy(alpha = 0.82f)),
            shape = RoundedCornerShape(24.dp),
            elevation = CardDefaults.cardElevation(8.dp)
        ) {
            Column(Modifier.padding(20.dp)) {
                FieldLabel("电脑地址")
                androidx.compose.material3.OutlinedTextField(
                    value = host, onValueChange = { host = it }, modifier = Modifier.fillMaxWidth(),
                    singleLine = true, shape = RoundedCornerShape(14.dp), placeholder = { Text("192.168.1.2:18120") }
                )
                Spacer(Modifier.height(14.dp))
                FieldLabel("6 位配对码")
                androidx.compose.material3.OutlinedTextField(
                    value = pin, onValueChange = { pin = it.filter(Char::isDigit).take(6) }, modifier = Modifier.fillMaxWidth(),
                    singleLine = true, shape = RoundedCornerShape(14.dp), placeholder = { Text("000000") }
                )
                Text(message, Modifier.padding(top = 12.dp), color = if (message.contains("失败") || message.contains("无法")) Color(0xFFD65362) else Muted, fontSize = 12.sp)
                Spacer(Modifier.height(18.dp))
                Button(
                    onClick = { connect() }, enabled = !connecting, modifier = Modifier.fillMaxWidth().height(50.dp),
                    colors = ButtonDefaults.buttonColors(containerColor = Blue), shape = RoundedCornerShape(15.dp)
                ) {
                    if (connecting) CircularProgressIndicator(Modifier.size(20.dp), color = Color.White, strokeWidth = 2.dp)
                    else Text("连接电脑", fontWeight = FontWeight.SemiBold)
                }
                OutlinedButton(
                    onClick = { scanner.launch(scanOptions()) },
                    modifier = Modifier.fillMaxWidth().padding(top = 10.dp).height(48.dp), shape = RoundedCornerShape(15.dp)
                ) { Text("扫描电脑配对码", color = Blue) }
            }
        }
    }
}

@Composable
private fun HomeScreen(session: SessionStore, onDisconnected: () -> Unit) {
    val context = androidx.compose.ui.platform.LocalContext.current
    val scope = rememberCoroutineScope()
    val connected by TransferRuntime.connected.collectAsState()
    val connectionText by TransferRuntime.connectionText.collectAsState()
    val transfers by TransferRuntime.transfers.collectAsState()
    var autoConnect by remember { mutableStateOf(session.autoConnectEnabled) }
    var actionMessage by remember { mutableStateOf("选择文件后将立即发送到电脑") }

    val filePicker = rememberLauncherForActivityResult(ActivityResultContracts.OpenMultipleDocuments()) { uris ->
        if (uris.isEmpty()) return@rememberLauncherForActivityResult
        scope.launch {
            for (uri in uris) {
                uploadUri(context, session, uri) { actionMessage = it }
            }
        }
    }

    LaunchedEffect(Unit) {
        if (session.autoConnectEnabled) SyncForegroundService.start(context, automatic = true)
    }

    Column(Modifier.fillMaxSize().padding(horizontal = 18.dp, vertical = 20.dp)) {
        Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
            Column(Modifier.weight(1f)) {
                Text("协作中心", fontSize = 27.sp, fontWeight = FontWeight.Bold, color = Ink)
                Text(session.serverName.ifBlank { session.host }, color = Muted, fontSize = 12.sp)
            }
            TextButton(onClick = {
                scope.launch {
                    withContext(Dispatchers.IO) { runCatching { CollabApi(session.host).logout(session.token) } }
                    SyncForegroundService.stop(context)
                    session.clearSession()
                    onDisconnected()
                }
            }) { Text("断开", color = Color(0xFFD65362)) }
        }
        Spacer(Modifier.height(16.dp))
        Card(colors = CardDefaults.cardColors(containerColor = Color.White.copy(alpha = 0.84f)), shape = RoundedCornerShape(22.dp)) {
            Column(Modifier.padding(18.dp)) {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Box(Modifier.size(12.dp).background(if (connected) Mint else Color(0xFFE16370), CircleShape))
                    Column(Modifier.padding(start = 12.dp).weight(1f)) {
                        Text(if (connected) "电脑已连接" else "等待电脑", fontWeight = FontWeight.SemiBold, color = Ink, fontSize = 17.sp)
                        Text(connectionText, color = Muted, fontSize = 11.sp)
                    }
                    OutlinedButton(onClick = { SyncForegroundService.start(context, automatic = false) }, shape = RoundedCornerShape(12.dp)) {
                        Text("重新连接", color = Blue, fontSize = 12.sp)
                    }
                }
                Spacer(Modifier.height(14.dp))
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Column(Modifier.weight(1f)) {
                        Text("下次自动连接", color = Ink, fontSize = 14.sp)
                        Text("同一局域网内自动查找已信任电脑", color = Muted, fontSize = 10.sp)
                    }
                    Switch(checked = autoConnect, onCheckedChange = {
                        autoConnect = it
                        session.autoConnectEnabled = it
                        if (it) SyncForegroundService.start(context, automatic = true)
                    })
                }
            }
        }
        Spacer(Modifier.height(14.dp))
        Card(colors = CardDefaults.cardColors(containerColor = Color.White.copy(alpha = 0.84f)), shape = RoundedCornerShape(22.dp)) {
            Column(Modifier.padding(18.dp)) {
                Text("发送到电脑", fontSize = 17.sp, fontWeight = FontWeight.SemiBold, color = Ink)
                Text(actionMessage, Modifier.padding(top = 4.dp), color = Muted, fontSize = 11.sp)
                Row(Modifier.padding(top = 14.dp), horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                    Button(
                        onClick = { filePicker.launch(arrayOf("*/*")) },
                        enabled = connected,
                        modifier = Modifier.weight(1f).height(48.dp),
                        colors = ButtonDefaults.buttonColors(containerColor = Blue),
                        shape = RoundedCornerShape(14.dp)
                    ) { Text("选择文件") }
                    OutlinedButton(
                        onClick = { filePicker.launch(arrayOf("image/*")) },
                        enabled = connected,
                        modifier = Modifier.weight(1f).height(48.dp),
                        shape = RoundedCornerShape(14.dp)
                    ) { Text("选择图片", color = Blue) }
                }
            }
        }
        Spacer(Modifier.height(16.dp))
        Row(verticalAlignment = Alignment.CenterVertically) {
            Text("传输动态", Modifier.weight(1f), fontSize = 17.sp, fontWeight = FontWeight.SemiBold, color = Ink)
            if (transfers.any { it.state == "completed" || it.state == "failed" }) {
                TextButton(onClick = TransferRuntime::clearFinished) { Text("清除已完成", color = Blue, fontSize = 11.sp) }
            }
        }
        if (transfers.isEmpty()) {
            Box(Modifier.fillMaxWidth().weight(1f), contentAlignment = Alignment.Center) {
                Text("双向传输任务会显示在这里", color = Muted, fontSize = 12.sp)
            }
        } else {
            LazyColumn(Modifier.fillMaxWidth().weight(1f), verticalArrangement = Arrangement.spacedBy(9.dp)) {
                items(transfers, key = { it.id }) { TransferCard(it) }
            }
        }
    }
}

@Composable
private fun TransferCard(transfer: MobileTransfer) {
    Card(colors = CardDefaults.cardColors(containerColor = Color.White.copy(alpha = 0.82f)), shape = RoundedCornerShape(17.dp)) {
        Column(Modifier.padding(14.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Column(Modifier.weight(1f)) {
                    Text(transfer.name, color = Ink, fontWeight = FontWeight.SemiBold, maxLines = 1, overflow = TextOverflow.Ellipsis)
                    Text("${transfer.direction} · ${transfer.message}", color = Muted, fontSize = 10.sp)
                }
                Text(
                    when (transfer.state) { "completed" -> "已完成"; "failed" -> "失败"; else -> "${(transfer.progress * 100).toInt()}%" },
                    color = when (transfer.state) { "completed" -> Mint; "failed" -> Color(0xFFD65362); else -> Blue },
                    fontSize = 11.sp, fontWeight = FontWeight.SemiBold
                )
            }
            LinearProgressIndicator(
                progress = { transfer.progress },
                modifier = Modifier.fillMaxWidth().padding(top = 10.dp).height(6.dp),
                color = if (transfer.state == "failed") Color(0xFFD65362) else Blue,
                trackColor = Color(0xFFDCEAF4),
            )
            Text("${formatBytes(transfer.transferred)} / ${formatBytes(transfer.total)}", Modifier.padding(top = 5.dp), color = Muted, fontSize = 9.sp)
        }
    }
}

private suspend fun uploadUri(context: Context, session: SessionStore, uri: Uri, report: (String) -> Unit) {
    val resolver = context.contentResolver
    val name = queryDisplayName(resolver, uri)
    val size = querySize(resolver, uri)
    if (size <= 0 || size > 1024L * 1024 * 1024) {
        report("$name 无法读取或超过 1 GB")
        return
    }
    val transferId = UUID.randomUUID().toString().replace("-", "")
    report("正在发送 $name")
    val ok = withContext(Dispatchers.IO) {
        runCatching {
            CollabApi(session.host).uploadFileStreaming(
                session.token,
                transferId,
                name,
                { resolver.openInputStream(uri) ?: error("无法读取文件") },
                size
            ) { done, total ->
                TransferRuntime.updateTransfer(MobileTransfer(
                    transferId, name, "手机 → 电脑", done, total, "transferring", "正在发送"
                ))
                showUploadProgress(context, transferId, name, done, total)
            }
        }.getOrDefault(false)
    }
    TransferRuntime.updateTransfer(MobileTransfer(
        transferId, name, "手机 → 电脑", if (ok) size else 0, size,
        if (ok) "completed" else "failed", if (ok) "电脑已接收" else "发送失败"
    ))
    showUploadCompleted(context, transferId, name, ok)
    report(if (ok) "$name 已发送到电脑" else "$name 发送失败，请检查连接")
}

private fun showUploadProgress(context: Context, id: String, name: String, done: Long, total: Long) {
    val manager = context.getSystemService(NotificationManager::class.java)
    manager.createNotificationChannel(NotificationChannel("collab_file", "X-Tool 文件传输", NotificationManager.IMPORTANCE_DEFAULT))
    val percent = if (total > 0) ((done * 100 / total).coerceIn(0, 100)).toInt() else 0
    manager.notify(id.hashCode(), NotificationCompat.Builder(context, "collab_file")
        .setSmallIcon(android.R.drawable.stat_sys_upload)
        .setContentTitle("正在发送到电脑")
        .setContentText("$name · $percent%")
        .setProgress(100, percent, false)
        .setOnlyAlertOnce(true)
        .setOngoing(true)
        .build())
}

private fun showUploadCompleted(context: Context, id: String, name: String, success: Boolean) {
    val manager = context.getSystemService(NotificationManager::class.java)
    manager.notify(id.hashCode(), NotificationCompat.Builder(context, "collab_file")
        .setSmallIcon(if (success) android.R.drawable.stat_sys_upload_done else android.R.drawable.stat_notify_error)
        .setContentTitle(if (success) "文件已发送到电脑" else "文件发送失败")
        .setContentText(name)
        .setAutoCancel(true)
        .build())
}

@Composable
private fun FieldLabel(text: String) = Text(text, color = Ink, fontSize = 12.sp, fontWeight = FontWeight.SemiBold)

private fun normalizeHost(value: String) = value.trim()
    .removePrefix("http://").removePrefix("https://")
    .substringBefore('/').trimEnd('/')

private fun parsePairUrl(value: String): Pair<String, String>? = try {
    val uri = Uri.parse(value)
    val host = uri.host ?: return null
    val port = if (uri.port > 0) uri.port else 18120
    val pin = uri.getQueryParameter("pin") ?: return null
    "$host:$port" to pin
} catch (_: Exception) {
    null
}

private fun scanOptions() = ScanOptions().apply {
    setPrompt("扫描电脑端 X-Tool 配对码")
    setBeepEnabled(false)
    setOrientationLocked(true)
    captureActivity = PortraitScanActivity::class.java
}

private fun queryDisplayName(resolver: ContentResolver, uri: Uri): String {
    var cursor: Cursor? = null
    return try {
        cursor = resolver.query(uri, arrayOf(OpenableColumns.DISPLAY_NAME), null, null, null)
        if (cursor != null && cursor.moveToFirst()) cursor.getString(0) ?: "未命名文件" else uri.lastPathSegment ?: "未命名文件"
    } catch (_: Exception) {
        uri.lastPathSegment ?: "未命名文件"
    } finally {
        cursor?.close()
    }
}

private fun querySize(resolver: ContentResolver, uri: Uri): Long {
    var cursor: Cursor? = null
    return try {
        cursor = resolver.query(uri, arrayOf(OpenableColumns.SIZE), null, null, null)
        if (cursor != null && cursor.moveToFirst() && !cursor.isNull(0)) cursor.getLong(0) else -1L
    } catch (_: Exception) {
        -1L
    } finally {
        cursor?.close()
    }
}

private fun formatBytes(bytes: Long): String = when {
    bytes >= 1024L * 1024 * 1024 -> "%.2f GB".format(bytes / 1024.0 / 1024 / 1024)
    bytes >= 1024L * 1024 -> "%.1f MB".format(bytes / 1024.0 / 1024)
    bytes >= 1024L -> "%.1f KB".format(bytes / 1024.0)
    else -> "${bytes.coerceAtLeast(0)} B"
}
