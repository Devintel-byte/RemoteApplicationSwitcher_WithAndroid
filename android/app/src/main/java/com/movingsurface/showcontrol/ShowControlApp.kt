package com.movingsurface.showcontrol

import android.app.Application
import android.os.Build
import com.movingsurface.showcontrol.alerts.AlertNotifier
import com.movingsurface.showcontrol.data.AppSettings
import com.movingsurface.showcontrol.data.ConnectionManager
import com.movingsurface.showcontrol.data.PcEndpoint
import com.movingsurface.showcontrol.data.SettingsRepository
import com.movingsurface.showcontrol.data.ShowSceneRunner
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.launch

/** App-wide singletons: settings storage, the live connections to every PC, and the scene runner. */
class ShowControlApp : Application() {

    val appScope = CoroutineScope(SupervisorJob() + Dispatchers.Default)
    lateinit var settings: SettingsRepository
        private set
    lateinit var connections: ConnectionManager
        private set
    lateinit var runner: ShowSceneRunner
        private set
    lateinit var notifier: AlertNotifier
        private set

    private val latest = MutableStateFlow<AppSettings?>(null)
    /** The current settings, for code outside the UI (services, notifications). Null until first read. */
    val settingsFlow: StateFlow<AppSettings?> get() = latest
    fun latestSettings(): AppSettings? = latest.value

    override fun onCreate() {
        super.onCreate()
        settings = SettingsRepository(this)
        connections = ConnectionManager(appScope, onAgentInfo = onInfo@{ pcId, info ->
            // Remember the PC's MAC while it's online so it can be woken later.
            val mac = info.macs.firstOrNull() ?: return@onInfo
            val pc = latest.value?.pcs?.firstOrNull { it.id == pcId } ?: return@onInfo
            if (pc.mac != mac) appScope.launch {
                settings.update { s -> s.copy(pcs = s.pcs.map { if (it.id == pcId) it.copy(mac = mac) else it }) }
            }
        })
        runner = ShowSceneRunner(appScope, connections) { latest.value }
        notifier = AlertNotifier(this, appScope, { latest.value }, connections.states)
        notifier.createChannels()
        appScope.launch { connections.alerts.collect { notifier.handle(it) } }
        appScope.launch {
            settings.settings.collect { s ->
                latest.value = s
                connections.sync(effectiveEndpoints(s), s.deviceName.ifBlank { defaultDeviceName() })
            }
        }
    }

    /** Hub PCs connect with the signed-in hub account; without one they're left out until sign-in. */
    private fun effectiveEndpoints(s: AppSettings): List<PcEndpoint> = s.pcs.mapNotNull { pc ->
        if (pc.hubAgentId == null) pc
        else s.hub?.let { hub -> pc.copy(host = hub.url, token = hub.token) }
    }

    companion object {
        fun defaultDeviceName(): String = "${Build.MANUFACTURER.replaceFirstChar { it.uppercase() }} ${Build.MODEL}"
    }
}
