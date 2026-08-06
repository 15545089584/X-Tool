package com.xtool.collab

import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
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
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.xtool.collab.data.CollabApi
import com.xtool.collab.data.SessionStore
import com.xtool.collab.ui.XToolCollabTheme
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

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

    Surface(modifier = Modifier.fillMaxSize(), color = MaterialTheme.colorScheme.background) {
        when (screen) {
            Screen.Pair -> PairScreen(
                initialHost = host,
                busy = false,
                onConnected = { newHost, newToken ->
                    host = newHost
                    token = newToken
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

@Composable
private fun PairScreen(
    initialHost: String,
    busy: Boolean,
    onConnected: (String, String) -> Unit
) {
    var host by remember { mutableStateOf(initialHost) }
    var pin by remember { mutableStateOf("") }
    var status by remember { mutableStateOf("请输入电脑端协作中心的地址（如 192.168.31.214:18120）与配对 PIN") }
    val scope = rememberCoroutineScope()
    var loading by remember { mutableStateOf(false) }

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
                Button(
                    onClick = {
                        val target = host.trim()
                        if (target.isEmpty() || pin.trim().isEmpty()) {
                            status = "请填写电脑地址与配对 PIN"
                            return@Button
                        }
                        loading = true
                        status = "正在配对…"
                        scope.launch {
                            val result = withContext(Dispatchers.IO) {
                                CollabApi(target).pair(pin.trim())
                            }
                            loading = false
                            if (result == null) {
                                status = "配对失败：请确认电脑服务已启动、地址与 PIN 正确、双方在同一网络"
                            } else {
                                status = "配对成功"
                                onConnected(target, result)
                            }
                        }
                    },
                    enabled = !loading,
                    colors = ButtonDefaults.buttonColors(containerColor = Color(0xFF4D7CFE)),
                    modifier = Modifier.fillMaxWidth().height(48.dp)
                ) {
                    if (loading) {
                        CircularProgressIndicator(modifier = Modifier.width(20.dp).height(20.dp), strokeWidth = 2.dp, color = Color.White)
                    } else {
                        Text("连接", fontSize = 15.sp, fontWeight = FontWeight.SemiBold)
                    }
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
    val scope = rememberCoroutineScope()
    var autoSync by remember { mutableStateOf(true) }
    var latestText by remember { mutableStateOf("") }
    var sendText by remember { mutableStateOf("") }
    var status by remember { mutableStateOf("已连接到 $host") }
    var lastSeq by remember { mutableStateOf(0L) }

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
                    Text("剪贴板同步", fontSize = 16.sp, fontWeight = FontWeight.SemiBold, color = Color(0xFF2F4A66))
                    Spacer(Modifier.weight(1f))
                    Switch(checked = autoSync, onCheckedChange = { autoSync = it })
                }
                Text(
                    if (autoSync) "开启后自动同步电脑与手机剪贴板（后台自动同步将在后续版本接入）" else "已关闭同步",
                    fontSize = 12.sp,
                    color = Color(0xFF7B93A8),
                    modifier = Modifier.padding(top = 6.dp)
                )

                Spacer(Modifier.height(14.dp))
                Text("最近收到", fontSize = 12.sp, fontWeight = FontWeight.SemiBold, color = Color(0xFF7B93A8))
                Text(
                    latestText.ifEmpty { "暂无内容，可点下方“拉取电脑剪贴板”测试" },
                    fontSize = 14.sp,
                    color = Color(0xFF405F7C),
                    modifier = Modifier.padding(top = 6.dp)
                )

                Spacer(Modifier.height(14.dp))
                OutlinedTextField(
                    value = sendText,
                    onValueChange = { sendText = it },
                    label = { Text("发送到电脑剪贴板") },
                    modifier = Modifier.fillMaxWidth()
                )
                Row(
                    modifier = Modifier.fillMaxWidth().padding(top = 12.dp),
                    horizontalArrangement = Arrangement.spacedBy(10.dp)
                ) {
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
                        modifier = Modifier.weight(1f).height(46.dp)
                    ) { Text("发送", fontSize = 14.sp, fontWeight = FontWeight.SemiBold) }
                    OutlinedButton(
                        onClick = {
                            scope.launch {
                                val (current, entries) = withContext(Dispatchers.IO) {
                                    CollabApi(host).pullClipboard(token, lastSeq)
                                }
                                lastSeq = current
                                val last = entries.lastOrNull()
                                if (last != null && last.kind == "text") {
                                    latestText = last.text
                                    status = "已拉取电脑剪贴板"
                                } else if (last != null && last.kind == "image") {
                                    latestText = "（收到图片，图片显示将在后续版本接入）"
                                    status = "已拉取电脑剪贴板图片"
                                } else {
                                    status = "电脑端暂无新内容"
                                }
                            }
                        },
                        modifier = Modifier.weight(1f).height(46.dp)
                    ) { Text("拉取电脑剪贴板", fontSize = 13.sp) }
                }
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
