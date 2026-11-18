package com.movingsurface.showcontrol.alerts

import android.annotation.SuppressLint
import android.app.Notification
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.os.Build
import android.os.IBinder
import androidx.core.app.NotificationCompat
import androidx.core.app.NotificationManagerCompat
import androidx.core.app.ServiceCompat
import androidx.core.content.ContextCompat
import com.movingsurface.showcontrol.R
import com.movingsurface.showcontrol.ShowControlApp
import com.movingsurface.showcontrol.data.AgentLiveState
import com.movingsurface.showcontrol.data.AppSettings
import kotlinx.coroutines.Job
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.flow.distinctUntilChanged
import kotlinx.coroutines.flow.filterNotNull
import kotlinx.coroutines.launch

/**
 * Keeps the app's connections to every PC alive with the app closed, so crash alerts still arrive.
 * Android only allows that for a foreground service, which shows a small, silent, ongoing notification
 * ("Watching 4 PCs · all running"). Turned on and off from Settings → Alerts.
 */
class MonitorService : Service() {

    private var job: Job? = null

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onCreate() {
        super.onCreate()
        val app = application as ShowControlApp
        startInForeground(buildNotification(app.latestSettings(), app.connections.states.value))
        job = app.appScope.launch {
            combine(app.settingsFlow.filterNotNull(), app.connections.states) { s, live -> Snapshot(summary(s, live), s.alerts.background, s, live) }
                .distinctUntilChanged { old, new -> old.text == new.text && old.on == new.on }
                .collect { snap ->
                    if (!snap.on) stopSelf() else refresh(snap.settings, snap.live)
                }
        }
    }

    private class Snapshot(val text: String, val on: Boolean, val settings: AppSettings, val live: Map<String, AgentLiveState>)

    @SuppressLint("MissingPermission") // an ongoing notification; harmless if notifications are off
    private fun refresh(s: AppSettings, live: Map<String, AgentLiveState>) {
        val nm = NotificationManagerCompat.from(this)
        if (nm.areNotificationsEnabled()) nm.notify(NOTIFICATION_ID, buildNotification(s, live))
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        if (intent?.action == ACTION_STOP) {
            val app = application as ShowControlApp
            app.appScope.launch { app.settings.update { it.copy(alerts = it.alerts.copy(background = false)) } }
            stopSelf()
            return START_NOT_STICKY
        }
        return START_STICKY
    }

    override fun onDestroy() {
        job?.cancel()
        super.onDestroy()
    }

    private fun startInForeground(n: Notification) {
        val type = if (Build.VERSION.SDK_INT >= 34) ServiceInfo.FOREGROUND_SERVICE_TYPE_SPECIAL_USE else 0
        ServiceCompat.startForeground(this, NOTIFICATION_ID, n, type)
    }

    private fun summary(s: AppSettings, live: Map<String, AgentLiveState>): String {
        val pcs = s.pcs.distinctBy { it.agentId ?: it.hubAgentId ?: it.id }
        if (pcs.isEmpty()) return "No PCs added yet"
        val states = pcs.map { live[it.id] }
        val online = states.count { it?.online == true }
        val crashed = states.sumOf { it?.crashedCount ?: 0 }
        val parts = mutableListOf("$online of ${pcs.size} ${if (pcs.size == 1) "PC" else "PCs"} online")
        if (crashed > 0) parts += "$crashed ${if (crashed == 1) "app" else "apps"} crashed"
        else if (online > 0) parts += "${states.sumOf { it?.runningCount ?: 0 }} apps running"
        return parts.joinToString(" · ")
    }

    private fun buildNotification(s: AppSettings?, live: Map<String, AgentLiveState>): Notification {
        val stop = PendingIntent.getService(
            this, 1, Intent(this, MonitorService::class.java).setAction(ACTION_STOP),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
        )
        return NotificationCompat.Builder(this, AlertNotifier.CH_MONITOR)
            .setSmallIcon(R.drawable.ic_stat_monitor)
            .setContentTitle("Watching your show PCs")
            .setContentText(if (s == null) "Starting…" else summary(s, live))
            .setOngoing(true)
            .setSilent(true)
            .setShowWhen(false)
            .setCategory(NotificationCompat.CATEGORY_SERVICE)
            .setContentIntent(AlertNotifier.openPcIntent(this, null, 0))
            .addAction(0, "Stop watching", stop)
            .setForegroundServiceBehavior(NotificationCompat.FOREGROUND_SERVICE_IMMEDIATE)
            .build()
    }

    companion object {
        private const val NOTIFICATION_ID = 4711
        private const val ACTION_STOP = "com.movingsurface.showcontrol.STOP_MONITOR"

        /** Call while the app is on screen (Android doesn't let a background app start this). */
        fun start(context: Context) {
            runCatching { ContextCompat.startForegroundService(context, Intent(context, MonitorService::class.java)) }
        }

        fun stop(context: Context) {
            context.stopService(Intent(context, MonitorService::class.java))
        }
    }
}
