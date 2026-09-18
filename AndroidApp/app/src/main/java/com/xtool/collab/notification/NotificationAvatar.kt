package com.xtool.collab.notification

import android.app.Notification
import android.content.Context
import android.graphics.Bitmap
import android.util.Base64
import androidx.core.app.NotificationCompat
import androidx.core.graphics.drawable.toBitmap
import java.io.ByteArrayOutputStream

/** 仅使用通知附带的图片或本机应用图标，不查询联系人、聊天库或远程头像地址。 */
object NotificationAvatar {
    fun read(context: Context, notification: Notification, packageName: String): Pair<String, String>? = runCatching {
        val personIcon = NotificationCompat.MessagingStyle.extractMessagingStyleFromNotification(notification)
            ?.messages?.lastOrNull()?.person?.icon
        val sender = runCatching { personIcon?.loadDrawable(context) }.getOrNull()
        val large = if (sender == null) runCatching { notification.getLargeIcon()?.loadDrawable(context) }.getOrNull() else null
        val drawable = sender ?: large ?: context.packageManager.getApplicationIcon(packageName)
        val kind = if (sender != null) "sender" else if (large != null) "notification" else "app"
        val bitmap = drawable.toBitmap(64, 64, Bitmap.Config.ARGB_8888)
        val output = ByteArrayOutputStream()
        bitmap.compress(Bitmap.CompressFormat.PNG, 100, output)
        val bytes = output.toByteArray()
        if (bytes.size > 16 * 1024) null else Base64.encodeToString(bytes, Base64.NO_WRAP) to kind
    }.getOrNull()
}
