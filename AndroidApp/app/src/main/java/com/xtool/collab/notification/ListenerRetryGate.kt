package com.xtool.collab.notification

/** 单调时钟退避：连续失败从 5 秒增加到最多 5 分钟，不随轮询频率反复绑定。 */
class ListenerRetryGate {
    private var next = 0L
    private var delay = 5_000L
    fun reset() { next = 0L; delay = 5_000L }
    fun allow(now: Long, manual: Boolean = false): Boolean {
        if (!manual && now < next) return false
        next = now + delay
        delay = (delay * 2).coerceAtMost(300_000L)
        return true
    }
}
