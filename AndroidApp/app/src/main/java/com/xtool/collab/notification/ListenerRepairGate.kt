package com.xtool.collab.notification

/** 先给普通重绑 15 秒；重建最多十分钟一次，连接恢复也不清除频率上限。 */
class ListenerRepairGate {
    private var missingSince: Long? = null
    private var lastRepair: Long? = null
    fun connected() { missingSince = null }
    fun shouldRepair(now: Long): Boolean {
        val since = missingSince ?: now.also { missingSince = it }
        if (now - since < 15_000) return false
        if (lastRepair?.let { now - it < 600_000 } == true) return false
        lastRepair = now
        return true
    }
}
