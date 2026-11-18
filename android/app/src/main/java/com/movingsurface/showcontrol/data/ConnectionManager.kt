package com.movingsurface.showcontrol.data

import okhttp3.OkHttpClient
import java.util.concurrent.TimeUnit
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asSharedFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update

/** Keeps exactly one [AgentConnection] per saved PC and merges their live state. */
class ConnectionManager(
    private val scope: CoroutineScope,
    private val onAgentInfo: (pcId: String, info: AgentInfo) -> Unit = { _, _ -> },
) {

    val http: OkHttpClient = OkHttpClient.Builder()
        .connectTimeout(4, TimeUnit.SECONDS)
        .readTimeout(0, TimeUnit.MILLISECONDS)
        .pingInterval(10, TimeUnit.SECONDS)
        .build()

    private val connections = mutableMapOf<String, AgentConnection>()
    private val _states = MutableStateFlow<Map<String, AgentLiveState>>(emptyMap())
    val states: StateFlow<Map<String, AgentLiveState>> = _states.asStateFlow()

    private val _screens = MutableStateFlow<Map<String, ScreenshotFrame>>(emptyMap())
    val screenshots: StateFlow<Map<String, ScreenshotFrame>> = _screens.asStateFlow()

    private val _messages = MutableSharedFlow<String>(extraBufferCapacity = 16)
    val messages: SharedFlow<String> = _messages.asSharedFlow()

    /** Crashes and PCs dropping off, for notifications (see AlertNotifier). */
    private val _alerts = MutableSharedFlow<PcAlert>(extraBufferCapacity = 64)
    val alerts: SharedFlow<PcAlert> = _alerts.asSharedFlow()

    @Synchronized
    fun sync(pcs: List<PcEndpoint>, clientName: String) {
        val wanted = pcs.associateBy { it.id }

        connections.keys.filter { it !in wanted }.forEach { id ->
            connections.remove(id)?.close()
            _states.update { it - id }
            _screens.update { it - id }
        }

        for (pc in pcs) {
            val existing = connections[pc.id]
            val same = existing != null && existing.pc.host == pc.host && existing.pc.port == pc.port &&
                existing.pc.token == pc.token && existing.pc.name == pc.name && existing.pc.hubAgentId == pc.hubAgentId
            if (same) continue
            existing?.close()
            val conn = AgentConnection(
                pc = pc,
                http = http,
                scope = scope,
                clientName = clientName,
                onState = { id, s -> _states.update { it + (id to s) } },
                onMessage = { _messages.tryEmit(it) },
                onAgentInfo = onAgentInfo,
                onScreenshot = { id, frame ->
                    // Keep the last good image when a later grab fails.
                    _screens.update { m ->
                        val prev = m[id]
                        m + (id to if (frame.bitmap == null && prev?.bitmap != null) prev.copy(error = frame.error) else frame)
                    }
                },
                onAlert = { _alerts.tryEmit(it) },
            )
            connections[pc.id] = conn
            conn.start()
        }
    }

    @Synchronized
    fun get(pcId: String): AgentConnection? = connections[pcId]

    /** Live view on: frames arrive in [screenshots]. Clears an old error so the view shows "connecting". */
    fun startStream(pcId: String, request: StreamRequest) {
        _screens.update { m -> m[pcId]?.let { m + (pcId to it.copy(error = null)) } ?: m }
        get(pcId)?.startStream(request)
    }

    fun stopStream(pcId: String) { get(pcId)?.stopStream() }

    /** Emit a one-off message (snackbar) from elsewhere in the app. */
    fun notify(text: String) { _messages.tryEmit(text) }

    @Synchronized
    fun reconnectAll() = connections.values.forEach { it.reconnectNow() }
}
