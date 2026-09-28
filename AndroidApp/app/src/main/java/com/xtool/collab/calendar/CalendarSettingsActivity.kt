package com.xtool.collab.calendar

import android.Manifest
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.xtool.collab.notification.NotificationBridge
import com.xtool.collab.service.SyncForegroundService
import com.xtool.collab.ui.XToolCollabTheme
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext

class CalendarSettingsActivity : ComponentActivity() {
    private var resumed by mutableIntStateOf(0)
    override fun onResume() { super.onResume(); resumed++ }
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val settings = CalendarSettings(this)
        setContent {
            XToolCollabTheme {
                var enabled by remember { mutableStateOf(settings.enabled) }
                var writeAllowed by remember { mutableStateOf(settings.allowWrite) }
                var writeGranted by remember { mutableStateOf(settings.writePermitted()) }
                var permitted by remember { mutableStateOf(settings.permitted()) }
                var choices by remember { mutableStateOf(emptyList<CalendarChoice>()) }
                var selected by remember { mutableStateOf(settings.selected) }
                var error by remember { mutableStateOf("") }
                val status by NotificationBridge.calendarStatus.collectAsState()
                val permission = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) {
                    permitted = it
                    if (it) { enabled = true; settings.enabled = true; SyncForegroundService.start(this) }
                    else error = "未获得日历访问权限。可在系统设置中允许后返回。"
                }
                val writePermission = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) {
                    writeGranted = it
                    if (it) { writeAllowed = true; settings.allowWrite = true; SyncForegroundService.start(this) }
                    else error = "未允许写入日历，仍可继续单向同步。"
                }
                LaunchedEffect(resumed, permitted) {
                    writeGranted = settings.writePermitted()
                    permitted = settings.permitted()
                    if (permitted) {
                        try { choices = withContext(Dispatchers.IO) { settings.calendars() }; error = "" }
                        catch (_: Exception) { error = "系统日历暂不可读取，请稍后返回重试" }
                    } else choices = emptyList()
                }
                val ink = Color(0xFF29445F)
                val muted = Color(0xFF72889D)
                LazyColumn(Modifier.fillMaxSize().background(Color(0xFFF0F6FB)).statusBarsPadding()
                    .navigationBarsPadding().padding(horizontal = 20.dp),
                    verticalArrangement = Arrangement.spacedBy(12.dp), contentPadding = PaddingValues(vertical = 16.dp)) {
                    item {
                        TextButton(onClick = { finish() }) { Text("‹ 返回设置", color = Color(0xFF4D7CFE)) }
                        Text("日程同步", fontSize = 28.sp, color = ink, fontWeight = FontWeight.Bold)
                        Text("将未来 120 天的系统日程同步到已配对电脑", color = muted, modifier = Modifier.padding(top = 8.dp))
                    }
                    item {
                        Card(shape = RoundedCornerShape(18.dp), colors = CardDefaults.cardColors(containerColor = Color.White)) {
                            Column(Modifier.padding(18.dp)) {
                                Row(verticalAlignment = Alignment.CenterVertically) {
                                    Text("启用日程同步", Modifier.weight(1f), color = ink, fontSize = 17.sp)
                                    Switch(enabled && permitted, onCheckedChange = {
                                        error = ""
                                        if (it && !settings.permitted()) permission.launch(Manifest.permission.READ_CALENDAR)
                                        else {
                                            enabled = it; settings.enabled = it
                                            SyncForegroundService.start(this@CalendarSettingsActivity)
                                        }
                                    })
                                }
                                Text(status, color = muted, fontSize = 13.sp)
                                Text("手机修改会自动同步到电脑。电脑按提前提醒时间显示宠物气泡，未开启宠物时使用 Windows 通知。",
                                    color = muted, fontSize = 13.sp, modifier = Modifier.padding(top = 10.dp))
                            }
                        }
                    }
                    item {
                        Card(shape = RoundedCornerShape(18.dp), colors = CardDefaults.cardColors(containerColor = Color.White)) {
                            Column(Modifier.padding(18.dp)) {
                                Row(verticalAlignment = Alignment.CenterVertically) {
                                    Text("允许电脑新建与编辑", Modifier.weight(1f), color = ink, fontSize = 16.sp)
                                    Switch(writeAllowed && writeGranted, enabled = enabled && permitted, onCheckedChange = {
                                        if (it && !settings.writePermitted()) writePermission.launch(Manifest.permission.WRITE_CALENDAR)
                                        else { writeAllowed = it; settings.allowWrite = it; SyncForegroundService.start(this@CalendarSettingsActivity) }
                                    })
                                }
                                Text("开启后申请写入权限，仅允许操作下方勾选且可写的日历。手机刚修改的日程会拒绝旧版本覆盖；电脑保存需等待手机回执。",
                                    color = muted, fontSize = 12.sp)
                            }
                        }
                    }
                    item { Text("选择同步的日历 · 已选 " + selected.size + " 个", color = ink, fontWeight = FontWeight.Bold) }
                    if (!permitted) item { Text("开启同步后授权，才能选择系统日历。", color = muted) }
                    items(choices, key = { it.id }) { calendar ->
                        Card(shape = RoundedCornerShape(14.dp), colors = CardDefaults.cardColors(containerColor = Color.White)) {
                            Row(Modifier.fillMaxWidth().padding(10.dp), verticalAlignment = Alignment.CenterVertically) {
                                Checkbox(calendar.id in selected, onCheckedChange = { checked ->
                                    selected = if (checked) selected + calendar.id else selected - calendar.id
                                    settings.selected = selected
                                })
                                Column {
                                    Row(verticalAlignment = Alignment.CenterVertically) {
                                        androidx.compose.foundation.Canvas(Modifier.size(9.dp)) {
                                            drawCircle(Color(android.graphics.Color.parseColor(calendar.color)))
                                        }
                                        Text(calendar.name, color = ink, modifier = Modifier.padding(start = 8.dp))
                                    }
                                    Text((if (calendar.canWrite) "可新建与编辑" else "只读日历") + if (calendar.visible) "" else " · 手机上已隐藏",
                                        fontSize = 12.sp, color = muted)
                                }
                            }
                        }
                    }
                    if (error.isNotBlank()) item { Text(error, color = Color(0xFFB74757)) }
                    item {
                        Text("首次请勾选日历。电脑保留 Windows 用户加密的本地副本，断开手机后仍可提醒已同步日程；离线期间的修改需重新连接才能更新。关闭同步或撤权后，下一次成功连接会清空电脑副本。",
                            color = muted, fontSize = 12.sp)
                        Text("全天事件按手机时区处理；厂商未公开的默认提醒会标注，不猜测时间。建议不要同时开启日历应用的普通通知同步，以免重复提醒。",
                            color = muted, fontSize = 12.sp, modifier = Modifier.padding(top = 8.dp))
                    }
                }
            }
        }
    }
}
