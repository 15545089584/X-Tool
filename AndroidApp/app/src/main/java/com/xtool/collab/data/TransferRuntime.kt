package com.xtool.collab.data

import android.content.Context
import android.content.SharedPreferences
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import org.json.JSONArray
import org.json.JSONObject

data class MobileTransfer(
    val id: String,
    val name: String,
    val direction: String,
    val transferred: Long,
    val total: Long,
    val state: String,
    val message: String,
    val openUri: String = ""
) {
    val progress: Float get() = if (total <= 0) 0f else (transferred.toFloat() / total).coerceIn(0f, 1f)
}

/** 前台页面与后台传输服务共享的轻量运行态，不在其中保存文件内容。 */
object TransferRuntime {
    private const val PreferencesName = "xtool_transfer_history"
    private const val HistoryKey = "finished_transfers"
    private const val MaxHistoryCount = 40

    private val _connected = MutableStateFlow(false)
    val connected: StateFlow<Boolean> = _connected.asStateFlow()

    private val _connectionText = MutableStateFlow("等待连接")
    val connectionText: StateFlow<String> = _connectionText.asStateFlow()

    private val _transfers = MutableStateFlow<List<MobileTransfer>>(emptyList())
    val transfers: StateFlow<List<MobileTransfer>> = _transfers.asStateFlow()
    private var preferences: SharedPreferences? = null

    @Synchronized
    fun initialize(context: Context) {
        if (preferences != null) return
        preferences = context.applicationContext.getSharedPreferences(PreferencesName, Context.MODE_PRIVATE)
        _transfers.value = loadFinishedTransfers()
    }

    fun updateConnection(connected: Boolean, text: String) {
        _connected.value = connected
        _connectionText.value = text
    }

    @Synchronized
    fun updateTransfer(transfer: MobileTransfer) {
        val current = _transfers.value.toMutableList()
        val index = current.indexOfFirst { it.id == transfer.id }
        if (index >= 0) current[index] = transfer else current.add(0, transfer)
        _transfers.value = current.take(MaxHistoryCount)
        if (transfer.state == "completed" || transfer.state == "failed") {
            persistFinishedTransfers(_transfers.value)
        }
    }

    @Synchronized
    fun clearFinished() {
        _transfers.value = _transfers.value.filterNot { it.state == "completed" || it.state == "failed" }
        persistFinishedTransfers(_transfers.value)
    }

    private fun loadFinishedTransfers(): List<MobileTransfer> {
        val serialized = preferences?.getString(HistoryKey, null) ?: return emptyList()
        return runCatching {
            val array = JSONArray(serialized)
            buildList {
                for (index in 0 until minOf(array.length(), MaxHistoryCount)) {
                    val item = array.optJSONObject(index) ?: continue
                    val state = item.optString("state")
                    if (state != "completed" && state != "failed") continue
                    add(MobileTransfer(
                        id = item.optString("id"),
                        name = item.optString("name"),
                        direction = item.optString("direction"),
                        transferred = item.optLong("transferred"),
                        total = item.optLong("total"),
                        state = state,
                        message = item.optString("message"),
                        openUri = item.optString("openUri")
                    ))
                }
            }
        }.getOrDefault(emptyList())
    }

    private fun persistFinishedTransfers(transfers: List<MobileTransfer>) {
        val array = JSONArray()
        transfers.asSequence()
            .filter { it.state == "completed" || it.state == "failed" }
            .take(MaxHistoryCount)
            .forEach { transfer ->
                array.put(JSONObject().apply {
                    put("id", transfer.id)
                    put("name", transfer.name)
                    put("direction", transfer.direction)
                    put("transferred", transfer.transferred)
                    put("total", transfer.total)
                    put("state", transfer.state)
                    put("message", transfer.message)
                    put("openUri", transfer.openUri)
                })
            }
        preferences?.edit()?.putString(HistoryKey, array.toString())?.apply()
    }
}
