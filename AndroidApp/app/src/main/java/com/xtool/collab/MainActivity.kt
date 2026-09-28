package com.xtool.collab

import android.Manifest
import android.app.NotificationChannel
import android.app.NotificationManager
import android.content.ContentValues
import android.content.ContentResolver
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.database.Cursor
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.os.ParcelFileDescriptor
import android.os.SystemClock
import android.provider.DocumentsContract
import android.provider.MediaStore
import android.provider.OpenableColumns
import android.webkit.MimeTypeMap
import android.widget.Toast
import androidx.activity.ComponentActivity
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.compose.setContent
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
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
import com.xtool.collab.data.RemoteFileEntry
import com.xtool.collab.data.RemoteFileRoot
import com.xtool.collab.data.SessionStore
import com.xtool.collab.data.TransferRuntime
import com.xtool.collab.service.SyncForegroundService
import com.xtool.collab.ui.XToolCollabTheme
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.async
import kotlinx.coroutines.awaitAll
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.io.BufferedOutputStream
import java.io.FileOutputStream
import java.io.FilterOutputStream
import java.util.UUID
import java.util.concurrent.atomic.AtomicLongArray

class MainActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        requestNotificationPermission()
        TransferRuntime.initialize(applicationContext)
        val session = SessionStore(applicationContext)
        if (session.autoConnectEnabled && session.token.isNotBlank()) {
            runCatching { SyncForegroundService.start(this, automatic = true) }
        }
        setContent {
            XToolCollabTheme {
                CollaborationApp(session)
            }
        }
        if (intent.getBooleanExtra("open_notifications", false)) {
            startActivity(Intent(this, com.xtool.collab.notification.NotificationSettingsActivity::class.java))
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
private const val ProgressNotificationIntervalMilliseconds = 750L

@Composable
private fun CollaborationApp(session: SessionStore) {
    var paired by remember { mutableStateOf(session.token.isNotBlank()) }
    var showingRemoteFiles by remember { mutableStateOf(false) }
    val background = Brush.verticalGradient(listOf(Color(0xFFEAF4FF), Color(0xFFF7FBFE), Color(0xFFEAFBF7)))
    Box(Modifier.fillMaxSize().background(background)) {
        if (paired) {
            if (showingRemoteFiles) {
                RemoteFilesScreen(session, onBack = { showingRemoteFiles = false })
            } else {
                HomeScreen(session, onOpenRemoteFiles = { showingRemoteFiles = true })
            }
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

    fun connect(targetHost: String = host, targetPin: String = pin, pairingQr: String? = null) {
        val normalizedHost = normalizeHost(targetHost)
        val normalizedPin = targetPin.trim()
        if (normalizedHost.isBlank() || normalizedPin.length != 6) {
            message = "请输入电脑地址和 6 位配对码"
            Toast.makeText(context, message, Toast.LENGTH_SHORT).show()
            return
        }
        connecting = true
        scope.launch {
            val result = withContext(Dispatchers.IO) {
                CollabApi(normalizedHost).pair(normalizedPin, session.deviceId, currentDeviceName())
            }
            connecting = false
            if (result.token == null) {
                message = result.error ?: "配对失败"
                Toast.makeText(context, message, Toast.LENGTH_SHORT).show()
                return@launch
            }
            session.host = normalizedHost
            // 以实际配对成功的地址为准，避免保存电脑另一块网卡或旧 Wi-Fi 的地址。
            session.lanHost = if (normalizedHost == result.tailscaleHost) result.host else normalizedHost
            session.tailscaleHost = result.tailscaleHost
            session.token = result.token
            session.pin = normalizedPin
            session.serverId = result.serverId
            session.serverName = result.serverName
            // 只使用用户扫描的证书凭据，不从明文配对响应学习通知身份。
            if (pairingQr != null) runCatching {
                com.xtool.collab.notification.NotificationBridge(context).bind(pairingQr)
            }.onFailure {
                Toast.makeText(context, "文件已连接，通知需在手机通知页面补全安全配对", Toast.LENGTH_LONG).show()
            }
            host = session.host
            pin = normalizedPin
            message = "连接成功，已保存电脑地址与配对信息"
            Toast.makeText(context, "已连接 ${session.serverName.ifBlank { session.host }}", Toast.LENGTH_SHORT).show()
            SyncForegroundService.start(context, automatic = false)
            onPaired()
        }
    }

    val scanner = rememberLauncherForActivityResult(ScanContract()) { result ->
        if (!result?.contents.isNullOrBlank()) {
            val parsed = parsePairUrl(result.contents)
            if (parsed == null) {
                message = "未识别为 X-Tool 配对码"
                Toast.makeText(context, message, Toast.LENGTH_SHORT).show()
            } else {
                host = parsed.first
                pin = parsed.second
                message = "已读取电脑地址，正在配对"
                connect(parsed.first, parsed.second, result.contents)
            }
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
        Text("首次扫码建立信任，之后可通过局域网或 Tailscale 连接", Modifier.padding(top = 8.dp), color = Muted, fontSize = 13.sp)
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
                Text(message, Modifier.padding(top = 12.dp), color = if (message.contains("失败") || message.contains("无法") || message.contains("不正确") || message.contains("错误")) Color(0xFFD65362) else Muted, fontSize = 12.sp)
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

private fun currentDeviceName(): String {
    val manufacturer = Build.MANUFACTURER.trim()
    val model = Build.MODEL.trim()
    if (model.equals("V2502A", ignoreCase = true)) return "Vivo X300 PRO"
    if (model.startsWith(manufacturer, ignoreCase = true)) return model
    val brand = manufacturer.replaceFirstChar { character -> character.uppercase() }
    return "$brand $model".trim()
}

@Composable
private fun HomeScreen(session: SessionStore, onOpenRemoteFiles: () -> Unit) {
    val context = androidx.compose.ui.platform.LocalContext.current
    val scope = rememberCoroutineScope()
    val connected by TransferRuntime.connected.collectAsState()
    val connectionText by TransferRuntime.connectionText.collectAsState()
    val transfers by TransferRuntime.transfers.collectAsState()
    var actionMessage by remember { mutableStateOf("选择文件后将立即发送到电脑") }

    val filePicker = rememberLauncherForActivityResult(ActivityResultContracts.OpenMultipleDocuments()) { uris ->
        if (uris.isEmpty()) return@rememberLauncherForActivityResult
        scope.launch {
            for (uri in uris) {
                runCatching { context.contentResolver.takePersistableUriPermission(uri, Intent.FLAG_GRANT_READ_URI_PERMISSION) }
                uploadUri(context, session, uri) { actionMessage = it }
            }
        }
    }

    LaunchedEffect(Unit) {
        if (session.autoConnectEnabled) SyncForegroundService.start(context, automatic = true)
    }

    LazyColumn(
        Modifier.fillMaxSize().padding(horizontal = 18.dp, vertical = 20.dp),
        verticalArrangement = Arrangement.spacedBy(9.dp)
    ) {
        item {
            Column {
        Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
            Column(Modifier.weight(1f)) {
                Text("协作中心", fontSize = 27.sp, fontWeight = FontWeight.Bold, color = Ink)
                Text(session.serverName.ifBlank { session.host }, color = Muted, fontSize = 12.sp)
            }
            TextButton(onClick = {
                scope.launch {
                    withContext(Dispatchers.IO) { runCatching { CollabApi(session.host).disconnect(session.token) } }
                    SyncForegroundService.stop(context)
                    com.xtool.collab.notification.PhoneNotificationListener.refresh()
                    TransferRuntime.updateConnection(false, "已手动断开，点击连接电脑可重新连接")
                    actionMessage = "已断开当前连接，电脑地址和配对信息已保留"
                    Toast.makeText(context, "已断开，仍保留这台可信电脑", Toast.LENGTH_SHORT).show()
                }
            }) { Text("断开", color = Color(0xFFD65362)) }
        }
        OutlinedButton(onClick = { context.startActivity(Intent(context, CollaborationSettingsActivity::class.java)) },
            shape = RoundedCornerShape(12.dp)) { Text("设置", color = Blue) }
        Spacer(Modifier.height(16.dp))
        Card(colors = CardDefaults.cardColors(containerColor = Color.White.copy(alpha = 0.84f)), shape = RoundedCornerShape(22.dp)) {
            Column(Modifier.padding(18.dp)) {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Box(Modifier.size(12.dp).background(if (connected) Mint else Color(0xFFE16370), CircleShape))
                    Column(Modifier.padding(start = 12.dp).weight(1f)) {
                        Text(if (connected) "电脑已连接" else "等待电脑", fontWeight = FontWeight.SemiBold, color = Ink, fontSize = 17.sp)
                        Text(connectionText, color = Muted, fontSize = 11.sp)
                    }
                    OutlinedButton(
                        onClick = {
                            SyncForegroundService.start(context, automatic = false)
                            actionMessage = "正在连接已信任电脑"
                        },
                        enabled = !connected,
                        shape = RoundedCornerShape(12.dp)
                    ) {
                        Text(if (connected) "已连接" else "连接电脑", color = Blue, fontSize = 12.sp)
                    }
                }
            }
        }
        Spacer(Modifier.height(14.dp))
        Card(
            modifier = Modifier.fillMaxWidth().clickable(enabled = connected, onClick = onOpenRemoteFiles),
            colors = CardDefaults.cardColors(containerColor = Color.White.copy(alpha = 0.84f)),
            shape = RoundedCornerShape(22.dp)
        ) {
            Row(Modifier.fillMaxWidth().padding(18.dp), verticalAlignment = Alignment.CenterVertically) {
                Box(
                    Modifier.size(42.dp).background(Color(0xFFE7F1FF), RoundedCornerShape(13.dp)),
                    contentAlignment = Alignment.Center
                ) {
                    Text("⌂", color = Blue, fontSize = 23.sp, fontWeight = FontWeight.Bold)
                }
                Column(Modifier.padding(start = 13.dp).weight(1f)) {
                    Text("远程文件", fontSize = 17.sp, fontWeight = FontWeight.SemiBold, color = Ink)
                    Text(
                        if (connected) "浏览电脑端明确授权的只读目录" else "连接电脑后可用",
                        Modifier.padding(top = 3.dp), color = Muted, fontSize = 10.sp
                    )
                }
                Text("进入 ›", color = if (connected) Blue else Muted, fontSize = 12.sp, fontWeight = FontWeight.SemiBold)
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
        Spacer(Modifier.height(12.dp))
        Card(colors = CardDefaults.cardColors(containerColor = Color.White.copy(alpha = 0.84f)), shape = RoundedCornerShape(22.dp)) {
            Row(
                Modifier.fillMaxWidth().padding(horizontal = 18.dp, vertical = 14.dp),
                verticalAlignment = Alignment.CenterVertically
            ) {
                Column(Modifier.weight(1f)) {
                    Text("电脑文件接收位置", fontSize = 15.sp, fontWeight = FontWeight.SemiBold, color = Ink)
                    Text("Download/XTool", Modifier.padding(top = 3.dp), color = Muted, fontSize = 10.sp)
                }
                OutlinedButton(
                    onClick = { openReceiveDirectory(context) },
                    shape = RoundedCornerShape(12.dp)
                ) {
                    Text("打开位置", color = Blue, fontSize = 12.sp)
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
            }
        }
        if (transfers.isEmpty()) {
            item {
                Box(Modifier.fillMaxWidth().height(120.dp), contentAlignment = Alignment.Center) {
                    Text("双向传输任务会显示在这里", color = Muted, fontSize = 12.sp)
                }
            }
        } else {
            items(transfers, key = { it.id }) { transfer ->
                TransferCard(transfer) { openTransfer(context, transfer) }
            }
        }
        item { Spacer(Modifier.height(11.dp)) }
    }
}

private data class RemotePathLevel(val name: String, val token: String)

@Composable
private fun RemoteFilesScreen(session: SessionStore, onBack: () -> Unit) {
    val context = androidx.compose.ui.platform.LocalContext.current
    val scope = rememberCoroutineScope()
    val connected by TransferRuntime.connected.collectAsState()
    var path by remember { mutableStateOf<List<RemotePathLevel>>(emptyList()) }
    var roots by remember { mutableStateOf<List<RemoteFileRoot>>(emptyList()) }
    var entries by remember { mutableStateOf<List<RemoteFileEntry>>(emptyList()) }
    var nextOffset by remember { mutableStateOf(-1) }
    var loading by remember { mutableStateOf(false) }
    var message by remember { mutableStateOf("正在读取电脑端授权目录") }
    var search by remember { mutableStateOf("") }
    var reloadKey by remember { mutableStateOf(0) }
    var activeDownloadToken by remember { mutableStateOf("") }
    val currentToken = path.lastOrNull()?.token

    LaunchedEffect(currentToken, reloadKey) {
        if (!connected) {
            message = "电脑连接已断开"
            roots = emptyList()
            entries = emptyList()
            return@LaunchedEffect
        }
        loading = true
        message = if (currentToken == null) "正在读取授权目录" else "正在读取文件列表"
        val result = withContext(Dispatchers.IO) {
            runCatching {
                val api = CollabApi(session.host)
                if (currentToken == null) api.listRemoteRoots(session.token)
                else api.listRemoteEntries(session.token, currentToken, 0, 100)
            }
        }
        result.onSuccess { value ->
            if (value is List<*>) {
                roots = value.filterIsInstance<RemoteFileRoot>()
                entries = emptyList()
                nextOffset = -1
                message = if (roots.isEmpty()) "电脑端尚未授权任何文件夹" else "仅显示电脑端明确授权的只读目录"
            } else if (value is com.xtool.collab.data.RemoteFilePage) {
                entries = value.entries
                nextOffset = value.nextOffset
                message = if (entries.isEmpty()) "此文件夹为空" else "${entries.size} 个项目"
            }
        }.onFailure { exception ->
            roots = emptyList()
            entries = emptyList()
            nextOffset = -1
            message = exception.message ?: "无法读取远程文件"
        }
        loading = false
    }

    Column(Modifier.fillMaxSize().padding(horizontal = 18.dp, vertical = 20.dp)) {
        Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
            TextButton(onClick = {
                if (path.isEmpty()) onBack() else path = path.dropLast(1)
                search = ""
            }) { Text("‹ 返回", color = Blue) }
            Column(Modifier.padding(start = 4.dp).weight(1f)) {
                Text(path.lastOrNull()?.name ?: "远程文件", fontSize = 24.sp, fontWeight = FontWeight.Bold, color = Ink,
                    maxLines = 1, overflow = TextOverflow.Ellipsis)
                Text(message, color = Muted, fontSize = 10.sp, maxLines = 1, overflow = TextOverflow.Ellipsis)
            }
            TextButton(onClick = { reloadKey++ }, enabled = !loading) { Text("刷新", color = Blue) }
        }

        if (path.isNotEmpty()) {
            androidx.compose.material3.OutlinedTextField(
                value = search,
                onValueChange = { search = it },
                modifier = Modifier.fillMaxWidth().padding(top = 10.dp),
                singleLine = true,
                shape = RoundedCornerShape(14.dp),
                placeholder = { Text("筛选当前文件夹") }
            )
        }

        if (loading) {
            Box(Modifier.fillMaxWidth().weight(1f), contentAlignment = Alignment.Center) {
                CircularProgressIndicator(color = Blue, strokeWidth = 3.dp)
            }
            return@Column
        }

        val visibleEntries = if (search.isBlank()) entries else entries.filter {
            it.name.contains(search.trim(), ignoreCase = true)
        }
        LazyColumn(
            Modifier.fillMaxWidth().weight(1f).padding(top = 14.dp),
            verticalArrangement = Arrangement.spacedBy(9.dp)
        ) {
            if (path.isEmpty()) {
                items(roots, key = { it.id }) { root ->
                    RemoteRootCard(root) {
                        path = listOf(RemotePathLevel(root.name, root.token))
                        search = ""
                    }
                }
            } else {
                items(visibleEntries, key = { it.token }) { entry ->
                    RemoteEntryCard(entry, activeDownloadToken == entry.token) {
                        if (entry.isDirectory) {
                            path = path + RemotePathLevel(entry.name, entry.token)
                            search = ""
                        } else if (activeDownloadToken.isEmpty()) {
                            activeDownloadToken = entry.token
                            message = "正在下载 ${entry.name}"
                            scope.launch {
                                val result = downloadRemoteFileToMediaStore(context, session, entry)
                                activeDownloadToken = ""
                                message = result.error ?: "${entry.name} 已保存到 Download/XTool/Remote"
                                if (result.uri != null) {
                                    Toast.makeText(context, "${entry.name} 下载完成", Toast.LENGTH_SHORT).show()
                                } else {
                                    Toast.makeText(context, result.error ?: "下载失败", Toast.LENGTH_LONG).show()
                                }
                            }
                        }
                    }
                }
                if (nextOffset >= 0 && search.isBlank()) {
                    item {
                        OutlinedButton(
                            onClick = {
                                if (loading || currentToken == null) return@OutlinedButton
                                loading = true
                                scope.launch {
                                    val result = withContext(Dispatchers.IO) {
                                        runCatching { CollabApi(session.host).listRemoteEntries(session.token, currentToken, nextOffset, 100) }
                                    }
                                    result.onSuccess { page ->
                                        entries = entries + page.entries
                                        nextOffset = page.nextOffset
                                        message = "${entries.size} 个项目"
                                    }.onFailure { message = it.message ?: "加载更多失败" }
                                    loading = false
                                }
                            },
                            modifier = Modifier.fillMaxWidth().height(46.dp),
                            shape = RoundedCornerShape(14.dp)
                        ) { Text("加载更多", color = Blue) }
                    }
                }
            }
        }

        Text(
            "只读访问 · 手机不能删除、移动、重命名或修改电脑文件",
            Modifier.fillMaxWidth().padding(top = 10.dp), color = Muted, fontSize = 9.sp
        )
    }
}

@Composable
private fun RemoteRootCard(root: RemoteFileRoot, onOpen: () -> Unit) {
    Card(
        modifier = Modifier.fillMaxWidth().clickable(onClick = onOpen),
        colors = CardDefaults.cardColors(containerColor = Color.White.copy(alpha = 0.84f)),
        shape = RoundedCornerShape(18.dp)
    ) {
        Row(Modifier.fillMaxWidth().padding(16.dp), verticalAlignment = Alignment.CenterVertically) {
            Box(Modifier.size(42.dp).background(Color(0xFFE7F1FF), RoundedCornerShape(13.dp)), contentAlignment = Alignment.Center) {
                Text("▣", color = Blue, fontSize = 20.sp)
            }
            Column(Modifier.padding(start = 13.dp).weight(1f)) {
                Text(root.name, color = Ink, fontWeight = FontWeight.SemiBold, maxLines = 1, overflow = TextOverflow.Ellipsis)
                Text("电脑端授权目录", Modifier.padding(top = 3.dp), color = Muted, fontSize = 10.sp)
            }
            Text("›", color = Blue, fontSize = 22.sp)
        }
    }
}

@Composable
private fun RemoteEntryCard(entry: RemoteFileEntry, downloading: Boolean, onClick: () -> Unit) {
    Card(
        modifier = Modifier.fillMaxWidth().clickable(enabled = !downloading, onClick = onClick),
        colors = CardDefaults.cardColors(containerColor = Color.White.copy(alpha = 0.84f)),
        shape = RoundedCornerShape(17.dp)
    ) {
        Row(Modifier.fillMaxWidth().padding(14.dp), verticalAlignment = Alignment.CenterVertically) {
            Box(
                Modifier.size(38.dp).background(if (entry.isDirectory) Color(0xFFE7F1FF) else Color(0xFFE7F8F5), RoundedCornerShape(12.dp)),
                contentAlignment = Alignment.Center
            ) {
                Text(if (entry.isDirectory) "▣" else "▤", color = if (entry.isDirectory) Blue else Mint, fontSize = 18.sp)
            }
            Column(Modifier.padding(start = 12.dp).weight(1f)) {
                Text(entry.name, color = Ink, fontWeight = FontWeight.SemiBold, maxLines = 1, overflow = TextOverflow.Ellipsis)
                Text(
                    if (entry.isDirectory) "文件夹" else "${formatBytes(entry.size)} · ${entry.modifiedAt.take(10)}",
                    Modifier.padding(top = 3.dp), color = Muted, fontSize = 9.sp
                )
            }
            if (downloading) {
                CircularProgressIndicator(Modifier.size(22.dp), color = Blue, strokeWidth = 2.dp)
            } else {
                Text(if (entry.isDirectory) "›" else "下载", color = Blue, fontSize = 11.sp, fontWeight = FontWeight.SemiBold)
            }
        }
    }
}

@Composable
private fun TransferCard(transfer: MobileTransfer, onOpen: () -> Unit) {
    val canOpen = transfer.state == "completed" && transfer.openUri.isNotBlank()
    Card(
        modifier = Modifier.clickable(enabled = canOpen, onClick = onOpen),
        colors = CardDefaults.cardColors(containerColor = Color.White.copy(alpha = 0.82f)),
        shape = RoundedCornerShape(17.dp)
    ) {
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
    if (size <= 0 || size > 5L * 1024 * 1024 * 1024) {
        report("$name 无法读取或超过 5 GB")
        return
    }
    val transferId = UUID.randomUUID().toString().replace("-", "")
    report("正在发送 $name")
    ensureTransferNotificationChannel(context)
    var lastProgressNotificationAt = 0L
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
                val now = SystemClock.elapsedRealtime()
                if (done >= total || now - lastProgressNotificationAt >= ProgressNotificationIntervalMilliseconds) {
                    lastProgressNotificationAt = now
                    showUploadProgress(context, transferId, name, done, total)
                }
            }
        }.getOrDefault(false)
    }
    TransferRuntime.updateTransfer(MobileTransfer(
        transferId, name, "手机 → 电脑", if (ok) size else 0, size,
        if (ok) "completed" else "failed", if (ok) "电脑已接收" else "发送失败",
        if (ok) uri.toString() else ""
    ))
    showUploadCompleted(context, transferId, name, ok)
    report(if (ok) "$name 已发送到电脑" else "$name 发送失败，请检查连接")
}

private data class RemoteDownloadResult(val uri: Uri? = null, val error: String? = null)

private const val RemoteParallelDownloadThresholdBytes = 32L * 1024 * 1024
private const val RemoteParallelDownloadSegments = 4

private class RemoteSegmentOutputStream(
    private val descriptor: ParcelFileDescriptor,
    output: FileOutputStream
) : FilterOutputStream(BufferedOutputStream(output, 2 * 1024 * 1024)) {
    override fun close() {
        try {
            super.close()
        } finally {
            descriptor.close()
        }
    }
}

private fun openRemoteSegmentOutput(
    resolver: ContentResolver,
    targetUri: Uri,
    offset: Long
): RemoteSegmentOutputStream {
    val descriptor = resolver.openFileDescriptor(targetUri, "rw")
        ?: error("无法打开手机下载文件")
    return try {
        val output = FileOutputStream(descriptor.fileDescriptor)
        output.channel.position(offset)
        RemoteSegmentOutputStream(descriptor, output)
    } catch (exception: Exception) {
        descriptor.close()
        throw exception
    }
}

private suspend fun downloadRemoteFileToMediaStore(
    context: Context,
    session: SessionStore,
    file: RemoteFileEntry
): RemoteDownloadResult = withContext(Dispatchers.IO) {
    if (Build.VERSION.SDK_INT < 29) {
        return@withContext RemoteDownloadResult(error = "远程文件下载需要 Android 10 或更高版本")
    }
    val transferId = UUID.randomUUID().toString().replace("-", "")
    ensureTransferNotificationChannel(context)
    var createdUri: Uri? = null
    try {
        val extension = file.extension.trimStart('.').lowercase()
        val mimeType = MimeTypeMap.getSingleton().getMimeTypeFromExtension(extension) ?: "application/octet-stream"
        val values = ContentValues().apply {
            put(MediaStore.Downloads.DISPLAY_NAME, file.name)
            put(MediaStore.Downloads.MIME_TYPE, mimeType)
            put(MediaStore.Downloads.RELATIVE_PATH, "Download/XTool/Remote")
            put(MediaStore.Downloads.IS_PENDING, 1)
        }
        val targetUri = context.contentResolver.insert(MediaStore.Downloads.EXTERNAL_CONTENT_URI, values)
            ?: return@withContext RemoteDownloadResult(error = "无法在手机下载目录中创建文件")
        createdUri = targetUri
        var lastNotificationAt = 0L
        val progressLock = Any()
        fun reportProgress(done: Long, total: Long) {
            synchronized(progressLock) {
                TransferRuntime.updateTransfer(MobileTransfer(
                    transferId, file.name, "电脑 → 手机", done, total, "transferring", "正在下载远程文件"
                ))
                val now = SystemClock.elapsedRealtime()
                if (done >= total || now - lastNotificationAt >= ProgressNotificationIntervalMilliseconds) {
                    lastNotificationAt = now
                    showRemoteDownloadProgress(context, transferId, file.name, done, total)
                }
            }
        }
        val api = CollabApi(session.host)
        val ok = if (file.size >= RemoteParallelDownloadThresholdBytes) {
            val segmentCount = RemoteParallelDownloadSegments
            val segmentSize = (file.size + segmentCount - 1) / segmentCount
            val segmentProgress = AtomicLongArray(segmentCount)
            coroutineScope {
                (0 until segmentCount).mapNotNull { index ->
                    val offset = segmentSize * index
                    if (offset >= file.size) return@mapNotNull null
                    val length = minOf(segmentSize, file.size - offset)
                    async(Dispatchers.IO) {
                        api.downloadRemoteFileSegment(
                            session.token,
                            transferId,
                            file,
                            offset,
                            length,
                            openOutput = {
                                openRemoteSegmentOutput(context.contentResolver, targetUri, offset)
                            },
                            onProgress = { segmentDone, _ ->
                                segmentProgress.set(index, segmentDone)
                                var aggregate = 0L
                                for (segmentIndex in 0 until segmentCount) {
                                    aggregate += segmentProgress.get(segmentIndex)
                                }
                                reportProgress(aggregate, file.size)
                            }
                        )
                    }
                }.awaitAll().all { it }
            }
        } else {
            api.downloadRemoteFile(
                session.token,
                transferId,
                file,
                openOutput = {
                    BufferedOutputStream(
                        context.contentResolver.openOutputStream(targetUri) ?: error("无法写入手机下载目录"),
                        2 * 1024 * 1024
                    )
                },
                onProgress = ::reportProgress
            )
        }
        if (!ok) throw java.io.IOException("下载内容不完整")
        context.contentResolver.update(targetUri, ContentValues().apply {
            put(MediaStore.Downloads.IS_PENDING, 0)
            put(MediaStore.Downloads.MIME_TYPE, mimeType)
        }, null, null)
        TransferRuntime.updateTransfer(MobileTransfer(
            transferId, file.name, "电脑 → 手机", file.size, file.size,
            "completed", "远程文件已保存", targetUri.toString()
        ))
        showRemoteDownloadCompleted(context, transferId, file.name, true)
        createdUri = null
        RemoteDownloadResult(uri = targetUri)
    } catch (exception: Exception) {
        createdUri?.let { runCatching { context.contentResolver.delete(it, null, null) } }
        TransferRuntime.updateTransfer(MobileTransfer(
            transferId, file.name, "电脑 → 手机", 0, file.size,
            "failed", exception.message ?: "远程文件下载失败"
        ))
        showRemoteDownloadCompleted(context, transferId, file.name, false)
        RemoteDownloadResult(error = exception.message ?: "远程文件下载失败")
    }
}

private fun openTransfer(context: Context, transfer: MobileTransfer) {
    if (transfer.openUri.isBlank()) return
    val uri = Uri.parse(transfer.openUri)
    val mimeType = context.contentResolver.getType(uri) ?: "*/*"
    val intent = Intent(Intent.ACTION_VIEW).apply {
        setDataAndType(uri, mimeType)
        addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_ACTIVITY_NEW_TASK)
    }
    runCatching { context.startActivity(intent) }.onFailure {
        Toast.makeText(context, "没有可打开此文件的应用", Toast.LENGTH_SHORT).show()
    }
}

private fun openReceiveDirectory(context: Context) {
    val directoryUri = DocumentsContract.buildDocumentUri(
        "com.android.externalstorage.documents",
        "primary:Download/XTool"
    )
    val directIntent = Intent(Intent.ACTION_VIEW).apply {
        setDataAndType(directoryUri, DocumentsContract.Document.MIME_TYPE_DIR)
        addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_ACTIVITY_NEW_TASK)
    }
    if (runCatching { context.startActivity(directIntent) }.isSuccess) {
        return
    }

    val fallbackIntent = Intent(Intent.ACTION_OPEN_DOCUMENT_TREE).apply {
        putExtra(DocumentsContract.EXTRA_INITIAL_URI, directoryUri)
        addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_ACTIVITY_NEW_TASK)
    }
    runCatching { context.startActivity(fallbackIntent) }.onFailure {
        Toast.makeText(context, "无法打开 Download/XTool，请从文件管理器进入下载目录", Toast.LENGTH_LONG).show()
    }
}

private fun ensureTransferNotificationChannel(context: Context) {
    context.getSystemService(NotificationManager::class.java).createNotificationChannel(
        NotificationChannel("collab_file", "X-Tool 文件传输", NotificationManager.IMPORTANCE_DEFAULT)
    )
}

private fun showUploadProgress(context: Context, id: String, name: String, done: Long, total: Long) {
    val manager = context.getSystemService(NotificationManager::class.java)
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

private fun showRemoteDownloadProgress(context: Context, id: String, name: String, done: Long, total: Long) {
    val manager = context.getSystemService(NotificationManager::class.java)
    val percent = if (total > 0) ((done * 100 / total).coerceIn(0, 100)).toInt() else 0
    manager.notify(id.hashCode(), NotificationCompat.Builder(context, "collab_file")
        .setSmallIcon(android.R.drawable.stat_sys_download)
        .setContentTitle("正在下载电脑文件")
        .setContentText("$name · $percent%")
        .setProgress(100, percent, false)
        .setOnlyAlertOnce(true)
        .setOngoing(true)
        .build())
}

private fun showRemoteDownloadCompleted(context: Context, id: String, name: String, success: Boolean) {
    context.getSystemService(NotificationManager::class.java).notify(
        id.hashCode(),
        NotificationCompat.Builder(context, "collab_file")
            .setSmallIcon(if (success) android.R.drawable.stat_sys_download_done else android.R.drawable.stat_notify_error)
            .setContentTitle(if (success) "远程文件下载完成" else "远程文件下载失败")
            .setContentText(name)
            .setAutoCancel(true)
            .build()
    )
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
