package com.xtool.collab.calendar

import android.Manifest
import android.content.ContentUris
import android.content.Context
import android.content.SharedPreferences
import android.content.pm.PackageManager
import android.database.ContentObserver
import android.os.Handler
import android.os.Looper
import android.provider.CalendarContract as C
import androidx.core.content.ContextCompat
import com.xtool.collab.data.SessionStore
import com.xtool.collab.data.TransferRuntime
import com.xtool.collab.notification.NotificationBridge
import kotlinx.coroutines.*
import kotlinx.coroutines.channels.Channel
import org.json.JSONArray
import org.json.JSONObject
import java.time.Instant
import java.time.ZoneId

class CalendarSettings(private val context: Context) {
    val prefs = context.getSharedPreferences("calendar_sync", Context.MODE_PRIVATE)
    var enabled: Boolean
        get() = prefs.getBoolean("enabled", false)
        set(value) { prefs.edit().putBoolean("enabled", value).apply() }
    var allowWrite: Boolean
        get() = prefs.getBoolean("allow_write", false)
        set(value) { prefs.edit().putBoolean("allow_write", value).apply() }
    var selected: Set<String>
        get() = prefs.getStringSet("calendars", emptySet()).orEmpty().toSet()
        set(value) { prefs.edit().putStringSet("calendars", value.toSet()).apply() }
    fun permitted() = ContextCompat.checkSelfPermission(context, Manifest.permission.READ_CALENDAR) == PackageManager.PERMISSION_GRANTED
    fun writePermitted() = ContextCompat.checkSelfPermission(context, Manifest.permission.WRITE_CALENDAR) == PackageManager.PERMISSION_GRANTED
    fun calendars() = if (permitted()) CalendarStore(context).calendars() else emptyList()
}

class CalendarSync(private val context: Context, scope: CoroutineScope) : AutoCloseable {
    private val settings = CalendarSettings(context)
    private val store = CalendarStore(context)
    private val bridge = NotificationBridge(context)
    private val wake = Channel<Unit>(Channel.CONFLATED)
    private var observed = false
    private val observer = object : ContentObserver(Handler(Looper.getMainLooper())) {
        override fun onChange(selfChange: Boolean) { wake.trySend(Unit) }
    }
    private val listener = SharedPreferences.OnSharedPreferenceChangeListener { _, key -> if (key != "revision") wake.trySend(Unit) }
    private val connectionJob = scope.launch { TransferRuntime.connected.collect { wake.trySend(Unit) } }
    private val job: Job
    init {
        settings.prefs.registerOnSharedPreferenceChangeListener(listener)
        job = scope.launch(Dispatchers.IO) {
            while (isActive) {
                try {
                    val active = settings.enabled && settings.permitted()
                    if (active && !observed) {
                        // 明确订阅各表，兼容只在具体表发布变更的系统日历。
                        listOf(C.Events.CONTENT_URI, C.Reminders.CONTENT_URI, C.Calendars.CONTENT_URI, C.Instances.CONTENT_URI)
                            .forEach { context.contentResolver.registerContentObserver(it, true, observer) }
                        observed = true
                    } else if (!active && observed) {
                        context.contentResolver.unregisterContentObserver(observer); observed = false
                    }
                    if (bridge.paired && !bridge.connectionPaused) {
                        val payload = snapshot(active)
                        if (active == (settings.enabled && settings.permitted()) &&
                            payload.optString("selection") == settings.selected.sorted().joinToString(",")) {
                            val body = payload.toString()
                            check(body.toByteArray(Charsets.UTF_8).size <= 512 * 1024)
                            if (bridge.sendCalendar(body) { response ->
                                if (response.optBoolean("refresh")) wake.trySend(Unit)
                                val commands = response.optJSONArray("commands") ?: JSONArray()
                                for (index in 0 until minOf(10, commands.length())) {
                                    if (!isActive || bridge.connectionPaused) break
                                    store.apply(commands.getJSONObject(index))
                                    wake.trySend(Unit)
                                }
                            }) {
                                NotificationBridge.calendarStatus.value = if (active)
                                    "已同步 " + payload.getJSONArray("events").length() + " 项 · " +
                                        java.text.SimpleDateFormat("HH:mm:ss", java.util.Locale.getDefault()).format(java.util.Date())
                                else "已通知电脑停止日程同步"
                            }
                        }
                    } else NotificationBridge.calendarStatus.value = if (!bridge.paired)
                        "请先在通知同步中绑定加密通道" else "连接已手动暂停"
                } catch (e: CancellationException) { throw e }
                catch (_: Exception) { NotificationBridge.calendarStatus.value = "日历同步未完成，正在重试；数量过多时请缩小所选范围" }
                // 连接时两秒兜底，系统先通知后落盘或漏通知都能较快补到；断开时降低频率。
                withTimeoutOrNull(if (TransferRuntime.connected.value && settings.enabled) 2000L else 15000L) { wake.receive() }
                delay(150)
                while (wake.tryReceive().isSuccess) { }
            }
        }
    }
    private fun snapshot(active: Boolean): JSONObject {
        val now = System.currentTimeMillis()
        val zone = ZoneId.systemDefault()
        val start = Instant.ofEpochMilli(now).atZone(zone).toLocalDate().minusDays(1).atStartOfDay(zone).toInstant().toEpochMilli()
        val end = Instant.ofEpochMilli(now).atZone(zone).toLocalDate().plusDays(120).atStartOfDay(zone).toInstant().toEpochMilli()
        val selected = settings.selected
        val events = JSONArray()
        val catalogs = JSONArray()
        if (active && selected.isNotEmpty()) {
            val calendars = settings.calendars().associateBy { it.id }
            check(selected.size <= 100)
            val ids = selected.filter { it in calendars }
            ids.forEach { id -> calendars[id]!!.let { c ->
                catalogs.put(JSONObject().put("id", c.id).put("name", c.name).put("color", c.color).put("canWrite", c.canWrite))
            } }
            if (ids.isNotEmpty()) {
                val uri = C.Instances.CONTENT_URI.buildUpon().also {
                    ContentUris.appendId(it, start); ContentUris.appendId(it, end)
                }.build()
                val fields = arrayOf(C.Instances.EVENT_ID, C.Instances.CALENDAR_ID, C.Instances.BEGIN,
                    C.Instances.END, C.Instances.ALL_DAY, C.Instances.DISPLAY_COLOR)
                val selection = C.Instances.CALENDAR_ID + " IN (" + ids.joinToString(",") { "?" } + ") AND (" +
                    C.Instances.STATUS + " IS NULL OR " + C.Instances.STATUS + " != 2) AND (" +
                    C.Instances.SELF_ATTENDEE_STATUS + " IS NULL OR " + C.Instances.SELF_ATTENDEE_STATUS + " != 2)"
                val cursor = context.contentResolver.query(uri, fields, selection, ids.toTypedArray(), C.Instances.BEGIN + " ASC")
                    ?: error("无法读取日程")
                val originals = mutableMapOf<Long, CalendarStore.Event?>()
                cursor.use {
                    while (it.moveToNext()) {
                        check(events.length() < 1000)
                        val eventId = it.getLong(0)
                        val calendarId = it.getLong(1).toString()
                        val begin = it.getLong(2); val allDay = it.getInt(4) == 1
                        val source = originals.getOrPut(eventId) { store.event(eventId) } ?: continue
                        if (source.values.getAsString(C.Events.DELETED) == "1") continue
                        val reminders = JSONArray()
                        var unresolved = false
                        source.reminders.filter { r -> r.getAsInteger(C.Reminders.METHOD) in listOf(0, 1) }
                            .map { r -> r.getAsInteger(C.Reminders.MINUTES) }.distinct().forEach { minutes ->
                                val at = CalendarReminderTime.at(begin, allDay, zone, minutes)
                                if (at == null) unresolved = true
                                else reminders.put(JSONObject().put("minutes", minutes).put("at", at))
                            }
                        val row = JSONObject().put("id", "$calendarId:$eventId:$begin").put("eventId", eventId).put("calendarId", calendarId)
                            .put("calendar", calendars[calendarId]?.name.orEmpty().take(160))
                            .put("color", CalendarLabels.color(it.getInt(5)))
                            .put("title", source.values.getAsString(C.Events.TITLE).orEmpty().take(300).ifBlank { "未命名日程" })
                            .put("location", source.values.getAsString(C.Events.EVENT_LOCATION).orEmpty().take(300))
                            .put("description", source.values.getAsString(C.Events.DESCRIPTION).orEmpty().take(2000))
                            .put("begin", begin).put("end", it.getLong(3)).put("allDay", allDay)
                            .put("version", source.version).put("recurring", source.recurring)
                            .put("rrule", source.values.getAsString(C.Events.RRULE).orEmpty().take(500))
                            .put("canEdit", calendars[calendarId]?.canWrite == true)
                            .put("reminders", reminders).put("unresolvedReminder", unresolved)
                        events.put(row)
                    }
                }
            }
        }
        val revision = maxOf(now, settings.prefs.getLong("revision", 0) + 1)
        check(settings.prefs.edit().putLong("revision", revision).commit())
        return JSONObject().put("schema", 2).put("deviceId", SessionStore(context).deviceId)
            .put("revision", revision).put("enabled", active).put("zone", zone.id)
            .put("selection", selected.sorted().joinToString(",")).put("canWrite", active && settings.allowWrite && settings.writePermitted())
            .put("calendars", catalogs).put("results", store.results())
            .put("rangeStart", start).put("rangeEnd", end).put("events", events)
    }
    override fun close() {
        job.cancel(); connectionJob.cancel()
        settings.prefs.unregisterOnSharedPreferenceChangeListener(listener)
        if (observed) runCatching { context.contentResolver.unregisterContentObserver(observer) }
        wake.close()
    }
}
