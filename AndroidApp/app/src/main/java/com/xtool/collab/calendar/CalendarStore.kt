package com.xtool.collab.calendar

import android.content.*
import android.database.Cursor
import android.provider.CalendarContract as C
import org.json.JSONArray
import org.json.JSONObject
import java.security.MessageDigest

data class CalendarChoice(val id: String, val name: String, val visible: Boolean,
    val color: String, val canWrite: Boolean)

/** 显示层翻译已知系统名称；自定义日历保持原名，不根据日程标题猜类型。 */
object CalendarLabels {
    fun name(raw: String): String = when (raw.lowercase(java.util.Locale.ROOT)) {
        "local calendar" -> "个人"
        "vivo work" -> "工作"
        "birthday" -> "生日"
        "vivo days matter" -> "倒数日"
        "vivo anniversary" -> "纪念日"
        "assistant" -> "日程助手"
        else -> raw.ifBlank { "未命名日历" }
    }
    fun color(value: Int) = "#%06X".format(value and 0xffffff)
}

class CalendarStore(private val context: Context) {
    private val resolver get() = context.contentResolver
    private val settings = CalendarSettings(context)
    private val journal = context.getSharedPreferences("calendar_operations", Context.MODE_PRIVATE)
    // 同步接口之外的事件字段不改动；版本包含重复规则及所有提醒，防止旧编辑覆盖新内容。
    private val fields = arrayOf(C.Events._ID, C.Events.CALENDAR_ID, C.Events.TITLE, C.Events.EVENT_LOCATION,
        C.Events.DESCRIPTION, C.Events.DTSTART, C.Events.DTEND, C.Events.ALL_DAY, C.Events.EVENT_TIMEZONE,
        C.Events.DURATION, C.Events.RRULE, C.Events.RDATE, C.Events.EXRULE, C.Events.EXDATE,
        C.Events.ORIGINAL_ID, C.Events.STATUS, C.Events.SELF_ATTENDEE_STATUS, C.Events.DELETED)
    data class Event(val values: ContentValues, val reminders: List<ContentValues>, val version: String) {
        val recurring get() = !values.getAsString(C.Events.RRULE).isNullOrBlank() ||
            !values.getAsString(C.Events.RDATE).isNullOrBlank() || values.get(C.Events.ORIGINAL_ID) != null
    }
    fun calendars(): List<CalendarChoice> {
        val result = mutableListOf<CalendarChoice>()
        val cursor = resolver.query(C.Calendars.CONTENT_URI, arrayOf(C.Calendars._ID,
            C.Calendars.CALENDAR_DISPLAY_NAME, C.Calendars.VISIBLE, C.Calendars.CALENDAR_COLOR,
            C.Calendars.CALENDAR_ACCESS_LEVEL), null, null, null) ?: error("日历读取失败")
        cursor.use { while (it.moveToNext()) result += CalendarChoice(it.getLong(0).toString(),
            CalendarLabels.name(it.getString(1).orEmpty()), it.getInt(2) == 1,
            CalendarLabels.color(it.getInt(3)), it.getInt(4) >= C.Calendars.CAL_ACCESS_CONTRIBUTOR) }
        return result
    }
    fun event(id: Long): Event? {
        val cursor = resolver.query(ContentUris.withAppendedId(C.Events.CONTENT_URI, id), fields,
            null, null, null) ?: error("日程读取失败")
        val values = cursor.use { if (!it.moveToFirst()) null else values(it, fields) } ?: return null
        val reminders = mutableListOf<ContentValues>()
        val cols = arrayOf(C.Reminders._ID, C.Reminders.EVENT_ID, C.Reminders.MINUTES, C.Reminders.METHOD)
        val rc = resolver.query(C.Reminders.CONTENT_URI, cols, C.Reminders.EVENT_ID + "=?",
            arrayOf(id.toString()), C.Reminders._ID + " ASC") ?: error("提醒读取失败")
        rc.use { while (it.moveToNext()) reminders += values(it, cols) }
        val canonical = JSONArray(fields.map { values[it]?.toString() ?: JSONObject.NULL })
        reminders.forEach { canonical.put(JSONArray(cols.map { key -> it[key]?.toString() ?: JSONObject.NULL })) }
        val hash = MessageDigest.getInstance("SHA-256").digest(canonical.toString().toByteArray(Charsets.UTF_8))
            .joinToString("") { "%02x".format(it) }
        return Event(values, reminders, hash)
    }
    private fun values(cursor: Cursor, columns: Array<String>): ContentValues = ContentValues().apply {
        columns.forEachIndexed { index, key ->
            if (cursor.isNull(index)) putNull(key) else put(key, cursor.getString(index))
        }
    }
    fun results(): JSONArray = JSONArray(journal.all.entries.take(256).mapNotNull {
        runCatching { JSONObject(it.value as String) }.getOrNull()
    })
    fun apply(command: JSONObject): JSONObject {
        val id = command.optString("id")
        require(id.matches(Regex("[a-fA-F0-9-]{36}")))
        journal.getString(id, null)?.let { return JSONObject(it) }
        var eventId = 0L
        val status = try {
            check(command.optString("deviceId") == com.xtool.collab.data.SessionStore(context).deviceId)
            val expires = command.getLong("expiresAt")
            check(expires >= System.currentTimeMillis() && expires <= System.currentTimeMillis() + 360000)
            if (!settings.enabled || !settings.permitted() || !settings.allowWrite || !settings.writePermitted())
                throw SecurityException()
            val calendarId = command.getString("calendarId")
            if (calendarId !in settings.selected || calendars().none { it.id == calendarId && it.canWrite })
                throw SecurityException()
            val kind = command.getString("kind")
            require(kind == "create" || kind == "update")
            val marker = "xtool://calendar/" + id
            // 创建命令的标记与事件在同一事务写入，进程在回执落盘前退出也不会重复创建。
            if (kind == "create") {
                resolver.query(C.Events.CONTENT_URI, arrayOf(C.Events._ID), C.Events.CUSTOM_APP_URI + "=?",
                    arrayOf(marker), null)?.use { if (it.moveToFirst()) eventId = it.getLong(0) }
            }
            if (eventId == 0L) {
                val title = command.getString("title").trim()
                val location = command.getString("location")
                val description = command.getString("description")
                require(title.isNotBlank() && title.length <= 300 && location.length <= 300 && description.length <= 2000)
                val begin = command.getLong("begin"); val end = command.getLong("end")
                require(begin in 946684800000..4102444800000 && end > begin && end <= 4102444800000)
                val allDay = command.getBoolean("allDay")
                if (allDay) require(begin % 86400000L == 0L && end % 86400000L == 0L)
                val minutes = command.getJSONArray("reminderMinutes").let { a ->
                    require(a.length() <= 10); (0 until a.length()).map { a.getInt(it) }.distinct()
                }
                require(minutes.all { it in -10080..525600 && it != -1 })
                val eventUri = if (kind == "update") ContentUris.withAppendedId(C.Events.CONTENT_URI, command.getLong("eventId")) else null
                val old = eventUri?.let { event(command.getLong("eventId")) ?: throw Conflict() }
                if (old != null && (old.version != command.getString("baseVersion") ||
                        old.values.getAsString(C.Events.CALENDAR_ID) != calendarId)) throw Conflict()
                val operations = arrayListOf<ContentProviderOperation>()
                if (old != null) {
                    // Assert 与更新一起进入 Provider 事务，防止读取版本以后手机再次编辑的竞态。
                    operations += ContentProviderOperation.newAssertQuery(eventUri!!).withValues(old.values).withExpectedCount(1).build()
                    operations += ContentProviderOperation.newAssertQuery(C.Reminders.CONTENT_URI)
                        .withSelection(C.Reminders.EVENT_ID + "=?", arrayOf(command.getLong("eventId").toString()))
                        .withExpectedCount(old.reminders.size).build()
                    old.reminders.forEach { r ->
                        operations += ContentProviderOperation.newAssertQuery(ContentUris.withAppendedId(C.Reminders.CONTENT_URI, r.getAsLong(C.Reminders._ID)))
                            .withValues(r).withExpectedCount(1).build()
                    }
                }
                val values = ContentValues().apply {
                    put(C.Events.TITLE, title); put(C.Events.EVENT_LOCATION, location); put(C.Events.DESCRIPTION, description)
                    put(C.Events.HAS_ALARM, if (minutes.isEmpty() && old?.reminders.orEmpty().none { it.getAsInteger(C.Reminders.METHOD) !in listOf(0, 1) }) 0 else 1)
                    if (old?.recurring != true) {
                        put(C.Events.DTSTART, begin); put(C.Events.DTEND, end); put(C.Events.ALL_DAY, if (allDay) 1 else 0)
                        put(C.Events.EVENT_TIMEZONE, if (allDay) "UTC" else java.time.ZoneId.systemDefault().id)
                        put(C.Events.EVENT_END_TIMEZONE, if (allDay) "UTC" else java.time.ZoneId.systemDefault().id)
                    }
                    if (old == null) {
                        put(C.Events.CALENDAR_ID, calendarId.toLong())
                        put(C.Events.CUSTOM_APP_PACKAGE, context.packageName); put(C.Events.CUSTOM_APP_URI, marker)
                        val rule = command.optString("rrule")
                        require(rule in listOf("", "FREQ=DAILY", "FREQ=WEEKLY", "FREQ=MONTHLY", "FREQ=YEARLY"))
                        if (rule.isNotEmpty()) {
                            put(C.Events.RRULE, rule); putNull(C.Events.DTEND)
                            put(C.Events.DURATION, if (allDay) "P" + ((end - begin) / 86400000) + "D" else "PT" + ((end - begin) / 1000) + "S")
                        }
                    }
                }
                val insertIndex = operations.size
                operations += if (old == null) ContentProviderOperation.newInsert(C.Events.CONTENT_URI).withValues(values).build()
                    else ContentProviderOperation.newUpdate(eventUri!!).withValues(values).withExpectedCount(1).build()
                if (old != null) {
                    eventId = command.getLong("eventId")
                    operations += ContentProviderOperation.newDelete(C.Reminders.CONTENT_URI)
                        .withSelection(C.Reminders.EVENT_ID + "=? AND (" + C.Reminders.METHOD + "=0 OR " + C.Reminders.METHOD + "=1)",
                            arrayOf(eventId.toString())).build()
                }
                minutes.forEach { m ->
                    val builder = ContentProviderOperation.newInsert(C.Reminders.CONTENT_URI)
                        .withValue(C.Reminders.MINUTES, m).withValue(C.Reminders.METHOD, C.Reminders.METHOD_ALERT)
                    if (old == null) builder.withValueBackReference(C.Reminders.EVENT_ID, insertIndex)
                    else builder.withValue(C.Reminders.EVENT_ID, eventId)
                    operations += builder.build()
                }
                val result = resolver.applyBatch(C.AUTHORITY, operations)
                if (old == null) eventId = ContentUris.parseId(result[insertIndex].uri!!)
            }
            "applied"
        } catch (_: Conflict) { "conflict" }
        catch (_: OperationApplicationException) { "conflict" }
        catch (_: SecurityException) { "forbidden" }
        catch (_: Exception) { "failed" }
        val result = JSONObject().put("id", id).put("status", status).put("eventId", eventId)
        val editor = journal.edit().putString(id, result.toString())
        if (journal.all.size > 250) journal.all.keys.take(journal.all.size - 200).forEach { editor.remove(it) }
        check(editor.commit()) { "写入回执未保存" }
        return result
    }
    private class Conflict : Exception()
}
