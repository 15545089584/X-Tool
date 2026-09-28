package com.xtool.collab.calendar

import org.junit.Assert.*
import org.junit.Test
import java.time.Instant
import java.time.ZoneId

class CalendarReminderTimeTest {
    private fun ms(value: String) = Instant.parse(value).toEpochMilli()
    @Test fun timedEventKeepsAbsoluteTime() {
        val begin = ms("2026-09-21T11:00:00Z")
        for (minutes in listOf(5, 15, 30, 60))
            assertEquals(begin - minutes * 60000L, CalendarReminderTime.at(begin, false, ZoneId.of("Asia/Shanghai"), minutes))
    }
    @Test fun allDayNegativeMinutesMeansMorningInPhoneZone() {
        assertEquals(ms("2026-10-06T01:00:00Z"), CalendarReminderTime.at(ms("2026-10-06T00:00:00Z"),
            true, ZoneId.of("Asia/Shanghai"), -540))
    }
    @Test fun allDayUsesEventDateOffsetAcrossDaylightSaving() {
        assertEquals(ms("2026-07-01T13:00:00Z"), CalendarReminderTime.at(ms("2026-07-01T00:00:00Z"),
            true, ZoneId.of("America/New_York"), -540))
        assertEquals(ms("2026-12-01T14:00:00Z"), CalendarReminderTime.at(ms("2026-12-01T00:00:00Z"),
            true, ZoneId.of("America/New_York"), -540))
    }
    @Test fun unknownDefaultIsNotGuessed() {
        assertNull(CalendarReminderTime.at(ms("2026-10-06T00:00:00Z"), true, ZoneId.of("Asia/Shanghai"), -1))
        assertNull(CalendarReminderTime.at(ms("2026-10-06T00:00:00Z"), true, ZoneId.of("Asia/Shanghai"), Int.MAX_VALUE))
    }
}
