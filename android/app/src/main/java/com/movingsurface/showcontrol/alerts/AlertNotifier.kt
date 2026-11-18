package com.movingsurface.showcontrol.alerts

import android.Manifest
import android.annotation.SuppressLint
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build
import androidx.core.app.NotificationCompat
import androidx.core.app.NotificationManagerCompat
import androidx.core.content.ContextCompat
import com.movingsurface.showcontrol.MainActivity
import com.movingsurface.showcontrol.R
import com.movingsurface.showcontrol.data.AgentLiveState
import com.movingsurface.showcontrol.data.AppSettings
import com.movingsurface.showcontrol.data.PcAlert
import java.text.DateFormat
import java.util.Date
import java.util.concurrent.ConcurrentHashMap
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.launch

/**
 * Turns [PcAlert]s into Android notifications: a heads-up when an app crashes (updated quietly when the
 * watchdog gets it running again) and, if wanted, when a PC has been unreachable for 30 seconds.
 * Tapping a notification opens that PC.
 */
class AlertNotifier(
    private val context: Context,
    private val scope: CoroutineScope,
    private val settings: () -> AppSettings?,
    private val live: StateFlow<Map<String, AgentLiveState>>,
) {
    private val nm = NotificationManagerCompat.from(context)
    private val offlineTimers = ConcurrentHashMap<String, Job>()
    /** Crash notifications currently showing, keyed pcId/appId, with the time of the crash. */
    private val crashShown = ConcurrentHashMap<String, Long>()
    private val offlineShown = ConcurrentHashMap.newKeySet<String>()

    fun createChannels() {
        val manager = context.getSystemService(NotificationManager::class.java) ?: return
        manager.createNotificationChannel(NotificationChannel(CH_CRASH, "App crashes", NotificationManager.IMPORTANCE_HIGH).apply {
            description = "An app on a show PC closed unexpectedly"
            enableVibration(true)
        })
        manager.createNotificationChannel(NotificationChannel(CH_OFFLINE, "PC offline", NotificationManager.IMPORTANCE_DEFAULT).apply {
            description = "A show PC stopped answering"
        })
        manager.createNotificationChannel(NotificationChannel(CH_MONITOR, "Background monitoring", NotificationManager.IMPORTANCE_LOW).apply {
            description = "Shown while Show Control keeps watching your PCs with the app closed"
            setShowBadge(false)
        })
    }

    fun permitted(): Boolean {
        if (Build.VERSION.SDK_INT >= 33 &&
            ContextCompat.checkSelfPermission(context, Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED
        ) return false
        return nm.areNotificationsEnabled()
    }

    fun handle(alert: PcAlert) {
        val s = settings() ?: return
        when (alert) {
            is PcAlert.AppCrashed -> {
                if (!s.alerts.crashes) return
                val key = "${alert.pcId}/${alert.appId}"
                crashShown[key] = System.currentTimeMillis()
                post(
                    id = key.hashCode(), channel = CH_CRASH, pcId = alert.pcId,
                    title = "${alert.appName} crashed",
                    text = listOfNotNull("On ${alert.pcName}", alert.message?.takeIf { it.isNotBlank() }).joinToString(" · "),
                    loud = true,
                )
            }

            is PcAlert.AppRecovered -> {
                val key = "${alert.pcId}/${alert.appId}"
                val at = crashShown.remove(key) ?: return
                post(
                    id = key.hashCode(), channel = CH_CRASH, pcId = alert.pcId,
                    title = "${alert.appName} is running again",
                    text = "On ${alert.pcName} · crashed at ${DateFormat.getTimeInstance(DateFormat.SHORT).format(Date(at))}",
                    loud = false,
                )
            }

            is PcAlert.PcOffline -> {
                if (!s.alerts.offline) return
                offlineTimers.remove(alert.pcId)?.cancel()
                offlineTimers[alert.pcId] = scope.launch {
                    delay(OFFLINE_GRACE_MS) // ignore Wi-Fi blips and PC restarts that come straight back
                    if (live.value[alert.pcId]?.online == true) return@launch
                    offlineShown += alert.pcId
                    post(
                        id = "offline/${alert.pcId}".hashCode(), channel = CH_OFFLINE, pcId = alert.pcId,
                        title = "${alert.pcName} is offline",
                        text = "Not answering for 30 seconds. Show Control keeps trying.",
                        loud = true,
                    )
                }
            }

            is PcAlert.PcOnline -> {
                offlineTimers.remove(alert.pcId)?.cancel()
                if (offlineShown.remove(alert.pcId)) {
                    post(
                        id = "offline/${alert.pcId}".hashCode(), channel = CH_OFFLINE, pcId = alert.pcId,
                        title = "${alert.pcName} is back online",
                        text = "Reconnected at ${DateFormat.getTimeInstance(DateFormat.SHORT).format(Date())}",
                        loud = false,
                    )
                }
            }
        }
    }

    @SuppressLint("MissingPermission") // checked in permitted()
    private fun post(id: Int, channel: String, pcId: String, title: String, text: String, loud: Boolean) {
        if (!permitted()) return
        val n = NotificationCompat.Builder(context, channel)
            .setSmallIcon(R.drawable.ic_stat_alert)
            .setContentTitle(title)
            .setContentText(text)
            .setStyle(NotificationCompat.BigTextStyle().bigText(text))
            .setCategory(if (loud) NotificationCompat.CATEGORY_ALARM else NotificationCompat.CATEGORY_STATUS)
            .setPriority(if (loud) NotificationCompat.PRIORITY_HIGH else NotificationCompat.PRIORITY_LOW)
            .setSilent(!loud)
            .setAutoCancel(true)
            .setContentIntent(openPcIntent(context, pcId, id))
            .build()
        nm.notify(id, n)
    }

    companion object {
        const val CH_CRASH = "crashes"
        const val CH_OFFLINE = "pc_offline"
        const val CH_MONITOR = "monitor"
        const val EXTRA_OPEN_PC = "open_pc"
        private const val OFFLINE_GRACE_MS = 30_000L

        fun openPcIntent(context: Context, pcId: String?, requestCode: Int): PendingIntent {
            val intent = Intent(context, MainActivity::class.java)
                .addFlags(Intent.FLAG_ACTIVITY_SINGLE_TOP or Intent.FLAG_ACTIVITY_CLEAR_TOP)
            if (pcId != null) intent.putExtra(EXTRA_OPEN_PC, pcId)
            return PendingIntent.getActivity(context, requestCode, intent, PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
        }
    }
}
