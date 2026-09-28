package com.xtool.collab

import android.content.Intent
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.xtool.collab.data.SessionStore
import com.xtool.collab.service.SyncForegroundService
import com.xtool.collab.screenshot.ScreenshotSyncCard
import com.xtool.collab.ui.XToolCollabTheme

/** 连接及内容同步偏好集中在此，首页只保留状态和文件操作。 */
class CollaborationSettingsActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val session = SessionStore(this)
        setContent {
            XToolCollabTheme {
                var autoConnect by remember { mutableStateOf(session.autoConnectEnabled) }
                val ink = Color(0xFF29445F)
                val muted = Color(0xFF72889D)
                LazyColumn(Modifier.fillMaxSize().background(Color(0xFFF0F6FB))
                    .statusBarsPadding().navigationBarsPadding().padding(horizontal = 18.dp),
                    verticalArrangement = Arrangement.spacedBy(12.dp), contentPadding = PaddingValues(vertical = 16.dp)) {
                    item {
                        Row(verticalAlignment = Alignment.CenterVertically) {
                            TextButton(onClick = { finish() }) { Text("‹ 返回", color = Color(0xFF4D7CFE)) }
                            Text("设置", fontSize = 26.sp, fontWeight = FontWeight.Bold, color = ink)
                        }
                        Text("连接与手机内容同步", Modifier.padding(start = 8.dp, top = 6.dp), color = muted, fontSize = 13.sp)
                    }
                    item {
                        Text("连接", color = muted, fontSize = 13.sp)
                        Card(Modifier.fillMaxWidth().padding(top = 8.dp), shape = RoundedCornerShape(18.dp),
                            colors = CardDefaults.cardColors(containerColor = Color.White)) {
                            Row(Modifier.padding(16.dp), verticalAlignment = Alignment.CenterVertically) {
                                Column(Modifier.weight(1f)) {
                                    Text("下次自动连接", color = ink, fontSize = 16.sp)
                                    Text("自动查找已信任的电脑", color = muted, fontSize = 12.sp)
                                }
                                Switch(checked = autoConnect, onCheckedChange = {
                                    autoConnect = it
                                    session.autoConnectEnabled = it
                                    if (it) SyncForegroundService.start(this@CollaborationSettingsActivity, automatic = true)
                                })
                            }
                        }
                    }
                    item {
                        Text("手机内容同步", color = muted, fontSize = 13.sp)
                        Card(Modifier.fillMaxWidth().padding(top = 8.dp), shape = RoundedCornerShape(18.dp),
                            colors = CardDefaults.cardColors(containerColor = Color.White)) {
                            Row(Modifier.padding(16.dp), verticalAlignment = Alignment.CenterVertically) {
                                Column(Modifier.weight(1f)) {
                                    Text("通知同步", color = ink, fontSize = 16.sp)
                                    Text("管理应用范围、预览和通知权限", color = muted, fontSize = 12.sp)
                                }
                                TextButton(onClick = {
                                    startActivity(Intent(this@CollaborationSettingsActivity,
                                        com.xtool.collab.notification.NotificationSettingsActivity::class.java))
                                }) { Text("管理 ›", color = Color(0xFF4D7CFE)) }
                            }
                        }
                    }
                    item {
                        Card(Modifier.fillMaxWidth(), shape = RoundedCornerShape(18.dp),
                            colors = CardDefaults.cardColors(containerColor = Color.White)) {
                            Row(Modifier.padding(16.dp), verticalAlignment = Alignment.CenterVertically) {
                                Column(Modifier.weight(1f)) {
                                    Text("日程同步", color = ink, fontSize = 16.sp)
                                    Text("只读同步系统日历与提前提醒", color = muted, fontSize = 12.sp)
                                }
                                TextButton(onClick = { startActivity(Intent(this@CollaborationSettingsActivity,
                                    com.xtool.collab.calendar.CalendarSettingsActivity::class.java)) }) {
                                    Text("管理 ›", color = Color(0xFF4D7CFE))
                                }
                            }
                        }
                    }
                    item { ScreenshotSyncCard() }
                    item { ScreenshotSyncCard(camera = true) }
                    item { Text("开关会自动保存。图片权限由截图和拍照同步共用，两个功能可分别开启。",
                        Modifier.padding(8.dp), color = muted, fontSize = 12.sp) }
                }
            }
        }
    }
}
