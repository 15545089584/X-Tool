package com.xtool.collab.screenshot

import android.Manifest
import android.content.Context
import android.content.pm.PackageManager
import android.os.Build
import androidx.core.content.ContextCompat
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.Alignment
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.xtool.collab.service.SyncForegroundService
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow

internal object ScreenshotSyncSettings {
    val status = MutableStateFlow("仅同步开启后新保存的截图")
    fun permission() = if (Build.VERSION.SDK_INT >= 33) Manifest.permission.READ_MEDIA_IMAGES else Manifest.permission.READ_EXTERNAL_STORAGE
    fun allowed(context: Context) = ContextCompat.checkSelfPermission(context, permission()) == PackageManager.PERMISSION_GRANTED
    fun prefs(context: Context) = context.getSharedPreferences("screenshot_sync", Context.MODE_PRIVATE)
    fun enabled(context: Context) = prefs(context).getBoolean("enabled", false)
    fun setEnabled(context: Context, enabled: Boolean) {
        prefs(context).edit().putBoolean("enabled", enabled).putLong("generation", System.currentTimeMillis()).apply()
        status.value = if (enabled) "等待新截图；请保持电脑连接" else "截图自动同步已关闭"
    }
}

@Composable
fun ScreenshotSyncCard() {
    val context = LocalContext.current
    var enabled by remember { mutableStateOf(ScreenshotSyncSettings.enabled(context)) }
    var allowed by remember { mutableStateOf(ScreenshotSyncSettings.allowed(context)) }
    val status by ScreenshotSyncSettings.status.collectAsState()
    fun enable() {
        enabled = true
        ScreenshotSyncSettings.setEnabled(context, true)
        SyncForegroundService.start(context, automatic = false)
    }
    val permission = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) { granted ->
        allowed = granted
        if (granted) enable() else {
            enabled = false
            ScreenshotSyncSettings.setEnabled(context, false)
            ScreenshotSyncSettings.status.value = "需要允许访问全部图片，才能自动读取之后的新截图；可在系统应用权限中修改"
        }
    }
    LaunchedEffect(Unit) {
        while (true) {
            allowed = ScreenshotSyncSettings.allowed(context)
            enabled = ScreenshotSyncSettings.enabled(context)
            delay(1000)
        }
    }
    Card(Modifier.fillMaxWidth().padding(top = 8.dp), shape = RoundedCornerShape(18.dp),
        colors = CardDefaults.cardColors(containerColor = Color.White)) {
        Column(Modifier.padding(16.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Text("截图自动同步", Modifier.weight(1f), color = Color(0xFF29445F), fontSize = 16.sp)
                Switch(checked = enabled && allowed, onCheckedChange = {
                    if (!it) {
                        enabled = false
                        ScreenshotSyncSettings.setEnabled(context, false)
                    } else if (ScreenshotSyncSettings.allowed(context)) enable()
                    else permission.launch(ScreenshotSyncSettings.permission())
                })
            }
            Text(if (enabled && !allowed) "图片权限已关闭，请重新授权" else status,
                color = Color(0xFF72889D), fontSize = 12.sp)
            Text("只发送新截图，不发送相册中的其他图片。电脑端可预览、复制或另存；断开连接可暂停同步。",
                Modifier.padding(top = 6.dp), color = Color(0xFF72889D), fontSize = 11.sp)
        }
    }
}
