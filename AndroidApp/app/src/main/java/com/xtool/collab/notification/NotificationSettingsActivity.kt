package com.xtool.collab.notification

import android.content.ComponentName
import android.content.Intent
import android.content.pm.ApplicationInfo
import android.os.Bundle
import android.provider.Settings
import android.service.notification.NotificationListenerService
import androidx.activity.ComponentActivity
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.compose.setContent
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.ui.Alignment
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.ImageBitmap
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.text.font.FontWeight
import androidx.core.graphics.drawable.toBitmap
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.journeyapps.barcodescanner.ScanContract
import com.journeyapps.barcodescanner.ScanOptions
import com.xtool.collab.PortraitScanActivity
import com.xtool.collab.ui.XToolCollabTheme
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.withContext

class NotificationSettingsActivity : ComponentActivity() {
    private var refreshVersion by mutableStateOf(0)
    private data class AppEntry(val packageName: String, val label: String, val system: Boolean, val icon: ImageBitmap?)
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContent { XToolCollabTheme { Page() } }
    }
    override fun onResume() {
        super.onResume()
        refreshVersion++
        NotificationListenerRecovery.ensureConnected(this)
        PhoneNotificationListener.refresh()
    }
    @Composable private fun Page() {
        val bridge = remember { NotificationBridge(this) }
        var enabled by remember { mutableStateOf(bridge.enabled) }
        var paired by remember { mutableStateOf(bridge.paired) }
        var message by remember { mutableStateOf("") }
        var listening by remember { mutableStateOf(PhoneNotificationListener.listening) }
        var granted by remember { mutableStateOf(NotificationListenerRecovery.hasAccess(this)) }
        val status by NotificationBridge.status.collectAsState()
        var applications by remember { mutableStateOf<List<AppEntry>>(emptyList()) }
        var showSystem by remember { mutableStateOf(false) }
        var loading by remember { mutableStateOf(true) }
        var filter by remember { mutableStateOf("") }
        val scanner = rememberLauncherForActivityResult(ScanContract()) { result ->
            result.contents?.let {
                runCatching { bridge.bind(it) }.onSuccess { paired = bridge.paired; enabled = bridge.enabled; message = "已补全安全连接。请选择应用；以后重连自动恢复，手动关闭的选择会保留。" }
                    .onFailure { message = "绑定失败：请扫描当前已连接电脑的新版协作中心配对码。" }
            }
        }
        LaunchedEffect(refreshVersion) {
            loading = true
            applications = withContext(Dispatchers.IO) {
                packageManager.getInstalledApplications(0).filter { it.packageName != packageName }.map {
                    AppEntry(it.packageName, runCatching { it.loadLabel(packageManager).toString() }.getOrDefault(it.packageName),
                        it.flags and ApplicationInfo.FLAG_SYSTEM != 0, runCatching { it.loadIcon(packageManager).toBitmap(96, 96).asImageBitmap() }.getOrNull())
                }.distinctBy { it.packageName }.sortedWith(compareBy<AppEntry> { it.system }.thenBy { it.label })
            }
            loading = false
        }
        LaunchedEffect(Unit) {
            while (true) { listening = PhoneNotificationListener.listening; granted = NotificationListenerRecovery.hasAccess(this@NotificationSettingsActivity); delay(1000) }
        }
        val ink = Color(0xFF243C56)
        val muted = Color(0xFF71849B)
        var connectionHelp by remember { mutableStateOf(false) }
        Surface(Modifier.fillMaxSize(), color = Color(0xFFF0F5FB)) {
            LazyColumn(Modifier.fillMaxSize(), contentPadding = PaddingValues(20.dp), verticalArrangement = Arrangement.spacedBy(14.dp)) {
                item {
                    Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.SpaceBetween) {
                        TextButton(onClick = { finish() }, contentPadding = PaddingValues(0.dp)) { Text("‹  协作中心") }
                        TextButton(onClick = { connectionHelp = !connectionHelp }) { Text("连接设置") }
                    }
                    Text("手机通知", fontSize = 30.sp, fontWeight = FontWeight.Bold, color = ink)
                    Text("让消息跨越屏幕，陪你专注当前工作", color = muted, fontSize = 13.sp, modifier = Modifier.padding(top = 6.dp, bottom = 6.dp))
                }
                item {
                    Card(shape = RoundedCornerShape(20.dp), colors = CardDefaults.cardColors(containerColor = Color.White)) {
                        Column(Modifier.fillMaxWidth().padding(18.dp)) {
                            Row(verticalAlignment = Alignment.CenterVertically) {
                                Text("●", color = if (listening && paired && status.startsWith("已加密同步")) Color(0xFF16B99B) else Color(0xFFE5A446), modifier = Modifier.padding(end = 8.dp))
                                Text(if (listening && paired && status.startsWith("已加密同步")) "已连接你的电脑" else if (listening && paired) "等待电脑连接" else if (granted) "正在恢复系统监听" else "完成连接设置", fontWeight = FontWeight.SemiBold, fontSize = 17.sp)
                            }
                            Text(if (!listening && granted) "已获得通知权限，系统监听暂未连接" else if (!listening) "先允许 X-Tool 读取通知，再选择要同步的应用" else if (!paired) "请连接电脑并补全安全配对" else status,
                                color = muted, fontSize = 12.sp, modifier = Modifier.padding(top = 8.dp))
                            if (!listening && granted) {
                                Button(onClick = { NotificationListenerRecovery.ensureConnected(this@NotificationSettingsActivity, manual = true) }, modifier = Modifier.fillMaxWidth().padding(top = 12.dp), shape = RoundedCornerShape(12.dp)) { Text("重新连接监听") }
                                Text("若仍未恢复，请在系统通知使用权中将 X-Tool协作关闭后重新开启。无需重新扫码。", color = muted, fontSize = 12.sp, modifier = Modifier.padding(top = 6.dp))
                                TextButton(onClick = { startActivity(Intent(Settings.ACTION_NOTIFICATION_LISTENER_SETTINGS)) }) { Text("打开系统通知使用权") }
                            }
                            if (!listening && !granted) {
                                Button(onClick = { startActivity(Intent(Settings.ACTION_NOTIFICATION_LISTENER_SETTINGS)) }, modifier = Modifier.fillMaxWidth().padding(top = 12.dp), shape = RoundedCornerShape(12.dp)) { Text("授权 X-Tool 读取通知") }
                                Text("在系统列表开启“X-Tool协作”，随后返回这里选择 QQ、微信等应用。", color = muted, fontSize = 12.sp, modifier = Modifier.padding(top = 6.dp))
                            }
                            HorizontalDivider(Modifier.padding(vertical = 14.dp), color = Color(0xFFEDF2F8))
                            Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
                                Column(Modifier.weight(1f)) {
                                    Text("自动同步", fontWeight = FontWeight.SemiBold)
                                    Text("重连自动恢复，手动关闭会保留", fontSize = 12.sp, color = muted)
                                }
                                Switch(checked = enabled, enabled = paired, onCheckedChange = { enabled = it; bridge.enabled = it; PhoneNotificationListener.refresh() })
                            }
                        }
                    }
                }
                if (connectionHelp || !paired) item {
                    Card(shape = RoundedCornerShape(16.dp), colors = CardDefaults.cardColors(containerColor = Color(0xFFE6EEFC))) {
                        Column(Modifier.fillMaxWidth().padding(16.dp)) {
                            Text("一次配对，持续连接", fontWeight = FontWeight.SemiBold)
                            Text("新版协作中心二维码已包含通知连接。已绑定的手机无需再扫；旧版连接只需补扫一次。", color = muted, fontSize = 12.sp, modifier = Modifier.padding(vertical = 8.dp))
                            OutlinedButton(onClick = { scanner.launch(ScanOptions().setCaptureActivity(PortraitScanActivity::class.java).setOrientationLocked(true).setBeepEnabled(false).setPrompt("扫描电脑协作中心配对码")) }) { Text(if (paired) "更新配对信息" else "扫描协作中心配对码") }
                            TextButton(onClick = { startActivity(Intent(Settings.ACTION_NOTIFICATION_LISTENER_SETTINGS)) }) { Text("管理 X-Tool 通知使用权") }
                            if (message.isNotBlank()) Text(message, fontSize = 12.sp)
                        }
                    }
                }
                item {
                    Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.SpaceBetween) {
                        Text("同步哪些应用", fontSize = 19.sp, fontWeight = FontWeight.SemiBold, color = ink)
                        TextButton(onClick = { refreshVersion++ }) { Text("刷新") }
                    }
                    Text(if (loading) "正在读取应用…" else "${applications.count { !it.system }} 个用户应用 · 选择后自动保存", color = muted, fontSize = 12.sp)
                    OutlinedTextField(value = filter, onValueChange = { filter = it }, placeholder = { Text("搜索应用名称或包名") }, singleLine = true,
                        shape = RoundedCornerShape(14.dp), modifier = Modifier.fillMaxWidth().padding(top = 12.dp),
                        colors = OutlinedTextFieldDefaults.colors(unfocusedContainerColor = Color.White, focusedContainerColor = Color.White, unfocusedBorderColor = Color(0xFFDDE6F2)))
                    Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.SpaceBetween) {
                        Text("包含系统应用", color = muted, fontSize = 12.sp)
                        Switch(checked = showSystem, onCheckedChange = { showSystem = it })
                    }
                }
                items(applications.filter { (showSystem || !it.system) && (it.label.contains(filter, true) || it.packageName.contains(filter, true)) }, key = { it.packageName }) { entry ->
                    var mode by remember(entry.packageName) { mutableStateOf(bridge.mode(entry.packageName)) }
                    Card(Modifier.fillMaxWidth(), shape = RoundedCornerShape(18.dp), colors = CardDefaults.cardColors(containerColor = Color.White)) {
                        Column(Modifier.padding(16.dp)) {
                            Row(verticalAlignment = Alignment.CenterVertically) {
                                entry.icon?.let { Image(it, contentDescription = null, modifier = Modifier.size(40.dp)) }
                                Column(Modifier.weight(1f).padding(start = 12.dp)) {
                                    Text(entry.label, fontWeight = FontWeight.SemiBold, color = ink)
                                    Text(if (mode == 0) "暂不同步" else if (mode == 1) "仅显示通知标题" else "显示标题与正文", fontSize = 12.sp, color = muted, modifier = Modifier.padding(top = 3.dp))
                                }
                            }
                            Row(Modifier.fillMaxWidth().padding(top = 10.dp), horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                                listOf("不同步", "仅标题", "标题和正文").forEachIndexed { value, title ->
                                    FilterChip(selected = mode == value, onClick = { mode = value; bridge.setMode(entry.packageName, value); PhoneNotificationListener.refresh() },
                                        label = { Text(title, fontSize = 11.sp, maxLines = 1) }, modifier = Modifier.weight(1f), shape = RoundedCornerShape(10.dp))
                                }
                            }
                        }
                    }
                }
                item {
                    Text("只同步选定应用的通知，不读取聊天记录。电脑锁屏时不弹新提醒；正文不写入同步日志。其他用户空间或分身应用需在对应空间配置。", color = muted, fontSize = 12.sp, lineHeight = 19.sp, modifier = Modifier.padding(vertical = 10.dp))
                    Text("若锁屏后同步中断，请检查 vivo 自启动与后台耗电管理。", color = muted, fontSize = 12.sp)
                }
            }
        }
    }
}
