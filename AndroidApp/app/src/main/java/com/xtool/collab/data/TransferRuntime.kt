package com.xtool.collab.data

import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow

data class MobileTransfer(
    val id: String,
    val name: String,
    val direction: String,
    val transferred: Long,
    val total: Long,
    val state: String,
    val message: String
) {
    val progress: Float get() = if (total <= 0) 0f else (transferred.toFloat() / total).coerceIn(0f, 1f)
}

/** 前台页面与后台传输服务共享的轻量运行态，不在其中保存文件内容。 */
object TransferRuntime {
    private val _connected = MutableStateFlow(false)
    val connected: StateFlow<Boolean> = _connected.asStateFlow()

    private val _connectionText = MutableStateFlow("等待连接")
    val connectionText: StateFlow<String> = _connectionText.asStateFlow()

    private val _transfers = MutableStateFlow<List<MobileTransfer>>(emptyList())
    val transfers: StateFlow<List<MobileTransfer>> = _transfers.asStateFlow()

    fun updateConnection(connected: Boolean, text: String) {
        _connected.value = connected
        _connectionText.value = text
    }

    fun updateTransfer(transfer: MobileTransfer) {
        val current = _transfers.value.toMutableList()
        val index = current.indexOfFirst { it.id == transfer.id }
        if (index >= 0) current[index] = transfer else current.add(0, transfer)
        _transfers.value = current.take(40)
    }

    fun clearFinished() {
        _transfers.value = _transfers.value.filterNot { it.state == "completed" || it.state == "failed" }
    }
}
