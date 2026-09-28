package com.xtool.collab.calendar

import java.time.Instant
import java.time.ZoneId
import java.time.ZoneOffset

/** 全天 UTC 日期转为手机本地零点，普通日程保持绝对时间。 */
object CalendarReminderTime {
    fun at(begin: Long, allDay: Boolean, zone: ZoneId, minutes: Int): Long? {
        if (minutes == -1 || minutes !in -10080..525600) return null
        val anchor = if (allDay) Instant.ofEpochMilli(begin).atZone(ZoneOffset.UTC).toLocalDate()
            .atStartOfDay(zone).toInstant().toEpochMilli() else begin
        return anchor - minutes * 60_000L
    }
}
