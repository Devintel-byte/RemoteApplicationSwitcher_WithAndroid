package com.movingsurface.showcontrol.data

import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.util.Log
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.Response
import okhttp3.WebSocket
import okhttp3.WebSocketListener
import okio.ByteString
import java.util.Base64
import java.util.concurrent.atomic.AtomicInteger
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.serialization.builtins.ListSerializer
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonObjectBuilder
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.contentOrNull
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import kotlinx.serialization.json.put

/** What the live view wants from a PC: which display, how big and how often. */
data class StreamRequest(val display: Int, val maxWidth: Int, val fps: Int, val quality: Int)

/**
 * One live WebSocket to one PC agent. Reconnects on its own with backoff and keeps an
 * up-to-date [AgentLiveState] that the UI observes.
 */
class AgentConnection(
    val pc: PcEndpoint,
    private val http: OkHttpClient,
    private val scope: CoroutineScope,
    private val clientName: String,
    private val onState: (pcId: String, state: AgentLiveState) -> Unit,
    private val onMessage: (String) -> Unit,
    private val onAgentInfo: (pcId: String, info: AgentInfo) -> Unit = { _, _ -> },
    private val onScreenshot: (pcId: String, frame: ScreenshotFrame) -> Unit = { _, _ -> },
    private val onAlert: (PcAlert) -> Unit = {},
) {
    private val lock = Any()
    private var socket: WebSocket? = null
    private var stopped = false
    private var attempt = 0
    private var reconnectJob: Job? = null
    private var state = AgentLiveState()
    private val requestIds = AtomicInteger()

    /** Set while a screen wants the live view; re-sent after every reconnect. */
    @Volatile private var stream: StreamRequest? = null

    /** App states from the last welcome/state, to spot crashes that happened while we were reconnecting. */
    private var knownApps: Map<String, AppState>? = null
    private var wasOnline = false

    fun start() = connect()

    fun close() {
        synchronized(lock) {
            stopped = true
            reconnectJob?.cancel()
            socket?.close(1000, "bye")
            socket = null
        }
    }

    /** Skip the backoff and try right now (pull-to-refresh, "Retry" button). */
    fun reconnectNow() {
        synchronized(lock) {
            if (stopped) return
            attempt = 0
            reconnectJob?.cancel()
            socket?.cancel()
            socket = null
        }
        update { it.copy(status = LinkStatus.CONNECTING, error = null) }
        connect()
    }

    // ---------- commands ----------

    fun startApp(appId: String) = send("start_app") { put("appId", appId) }
    fun stopApp(appId: String, force: Boolean = false) = send("stop_app") { put("appId", appId); put("force", force) }
    fun restartApp(appId: String) = send("restart_app") { put("appId", appId) }
    fun runScene(sceneId: String) = send("run_scene") { put("sceneId", sceneId) }
    fun stopAll() = send("stop_all")
    fun power(action: String) = send("power") { put("action", action) }
    fun wake(mac: String) = send("wake") { put("mac", mac) }

    /** Asks for a single screen grab (older agents without streaming). Quiet when offline. */
    fun requestScreenshot(display: Int, maxWidth: Int) {
        if (!state.online || state.agent?.canScreenshot != true) return
        send("screenshot", quiet = true) { put("display", display); put("maxWidth", maxWidth) }
    }

    /** True when this PC can push a live stream (agent 1.3 or later). */
    val canStream: Boolean get() = state.agent?.canStream == true

    /** Starts (or retunes) the live view. The PC pushes frames until [stopStream] or disconnect. */
    fun startStream(request: StreamRequest) {
        if (stream == request) return
        stream = request
        if (state.online && canStream) sendStreamStart(request)
    }

    fun stopStream() {
        if (stream == null) return
        stream = null
        if (state.online) send("stream_stop", quiet = true)
    }

    private fun sendStreamStart(r: StreamRequest) {
        send("stream_start", quiet = true, id = STREAM_ID) {
            put("display", r.display)
            put("maxWidth", r.maxWidth)
            put("fps", r.fps)
            put("quality", r.quality)
        }
    }

    private fun send(type: String, quiet: Boolean = false, id: String? = null, extra: JsonObjectBuilder.() -> Unit = {}): Boolean {
        val ws = synchronized(lock) { socket }
        if (ws == null || !state.online) {
            if (!quiet) onMessage("${pc.name} is offline")
            return false
        }
        val msg = buildJsonObject {
            put("type", type)
            put("id", id ?: requestIds.incrementAndGet().toString())
            extra()
        }
        return ws.send(msg.toString())
    }

    // ---------- socket ----------

    private fun connect() {
        // Hub PCs: host holds the hub's base URL here (set by ShowControlApp from the signed-in account).
        val url = if (pc.hubAgentId != null) HubClient.socketUrl(pc.host, pc.hubAgentId) else "ws://${pc.host}:${pc.port}/ws"
        val request = try {
            Request.Builder().url(url).build()
        } catch (e: IllegalArgumentException) {
            update { it.copy(status = LinkStatus.OFFLINE, error = "Invalid address ${pc.host}") }
            return
        }
        synchronized(lock) {
            if (stopped) return
            socket = http.newWebSocket(request, Listener())
        }
    }

    private inner class Listener : WebSocketListener() {
        override fun onOpen(webSocket: WebSocket, response: Response) {
            synchronized(lock) { attempt = 0 }
            webSocket.send(buildJsonObject {
                put("type", "hello")
                put("id", "hello")
                put("token", pc.token)
                put("clientName", clientName)
            }.toString())
        }

        override fun onMessage(webSocket: WebSocket, text: String) {
            if (!isCurrent(webSocket)) return
            try {
                handle(AppJson.parseToJsonElement(text).jsonObject)
            } catch (e: Exception) {
                Log.w(TAG, "Bad message from ${pc.name}: $text", e)
            }
        }

        /** A live-stream frame: 12-byte header then a JPEG (see the agent's ScreenStream.cs). */
        override fun onMessage(webSocket: WebSocket, bytes: ByteString) {
            if (!isCurrent(webSocket)) return
            val data = bytes.toByteArray()
            if (data.size <= HEADER || data[0] != 'S'.code.toByte() || data[1].toInt() != 1) return
            val display = data[2].toInt() and 0xFF
            val seq = ((data[4].toLong() and 0xFF) shl 24) or ((data[5].toLong() and 0xFF) shl 16) or
                ((data[6].toLong() and 0xFF) shl 8) or (data[7].toLong() and 0xFF)
            val width = ((data[8].toInt() and 0xFF) shl 8) or (data[9].toInt() and 0xFF)
            val height = ((data[10].toInt() and 0xFF) shl 8) or (data[11].toInt() and 0xFF)
            val wanted = stream
            if (wanted != null && wanted.display == display) {
                val opts = BitmapFactory.Options().apply { inPreferredConfig = Bitmap.Config.RGB_565 }
                val bitmap = BitmapFactory.decodeByteArray(data, HEADER, data.size - HEADER, opts)
                if (bitmap != null) {
                    onScreenshot(pc.id, ScreenshotFrame(
                        display = display, bitmap = bitmap, width = width, height = height,
                        displays = state.agent?.displays ?: emptyList(), live = true, seq = seq,
                    ))
                }
            }
            // Ack once the frame is decoded and handed to the UI: the PC sends the next one only then,
            // so a slow link gets fewer, fresher frames instead of a growing delay.
            webSocket.send("{\"type\":\"stream_ack\",\"seq\":$seq}")
        }

        override fun onClosing(webSocket: WebSocket, code: Int, reason: String) {
            webSocket.close(1000, null)
        }

        override fun onClosed(webSocket: WebSocket, code: Int, reason: String) = dropped(webSocket, null)

        override fun onFailure(webSocket: WebSocket, t: Throwable, response: Response?) = dropped(webSocket, t.message)
    }

    private fun isCurrent(ws: WebSocket) = synchronized(lock) { ws === socket }

    private fun dropped(ws: WebSocket, reason: String?) {
        val delayMs: Long
        synchronized(lock) {
            if (ws !== socket) return
            socket = null
            if (stopped || state.status == LinkStatus.UNAUTHORIZED) return
            delayMs = minOf(10_000L, 1_000L shl minOf(attempt, 4))
            attempt++
        }
        update { it.copy(status = LinkStatus.OFFLINE, error = reason, sceneRun = null) }
        if (wasOnline) {
            wasOnline = false
            onAlert(PcAlert.PcOffline(pc.id, pc.name))
        }
        reconnectJob = scope.launch {
            delay(delayMs)
            connect()
        }
    }

    private fun handle(msg: JsonObject) {
        when (msg.str("type")) {
            "welcome", "state" -> {
                val agent = msg["agent"]?.let { AppJson.decodeFromJsonElement(AgentInfo.serializer(), it) }
                val apps = msg["apps"]?.let { AppJson.decodeFromJsonElement(ListSerializer(AppInfo.serializer()), it) } ?: emptyList()
                val scenes = msg["scenes"]?.let { AppJson.decodeFromJsonElement(ListSerializer(SceneInfo.serializer()), it) } ?: emptyList()
                val stats = msg["stats"]?.let { AppJson.decodeFromJsonElement(Stats.serializer(), it) }
                val role = msg.str("role")
                update { it.copy(status = LinkStatus.ONLINE, agent = agent, apps = apps, scenes = scenes, stats = stats, error = null, role = role ?: it.role) }
                agent?.let { onAgentInfo(pc.id, it) }

                // Crashes that happened while we were away. (Nothing on the very first connect: those aren't news.)
                knownApps?.let { before ->
                    apps.filter { it.appState == AppState.CRASHED && before[it.id] != AppState.CRASHED }
                        .forEach { onAlert(PcAlert.AppCrashed(pc.id, pc.name, it.id, it.name, it.message)) }
                }
                knownApps = apps.associate { it.id to it.appState }
                if (msg.str("type") == "welcome") {
                    if (!wasOnline) onAlert(PcAlert.PcOnline(pc.id, pc.name))
                    wasOnline = true
                    stream?.let { if (agent?.canStream == true) sendStreamStart(it) }
                }
            }

            "app_status" -> {
                val appId = msg.str("appId") ?: return
                val previous = state.apps.firstOrNull { it.id == appId }
                val newState = AppState.parse(msg.str("state"))
                update { s ->
                    s.copy(apps = s.apps.map { a ->
                        if (a.id != appId) a
                        else a.copy(state = msg.str("state") ?: a.state, pid = msg.int("pid"), since = msg.str("since"), message = msg.str("message"))
                    })
                }
                knownApps = knownApps?.plus(appId to newState)
                val name = previous?.name ?: appId
                if (newState == AppState.CRASHED && previous?.appState != AppState.CRASHED) {
                    onAlert(PcAlert.AppCrashed(pc.id, pc.name, appId, name, msg.str("message")))
                } else if (newState == AppState.RUNNING) {
                    onAlert(PcAlert.AppRecovered(pc.id, pc.name, appId, name))
                }
            }

            "scene_progress" -> {
                val run = AppJson.decodeFromJsonElement(SceneRun.serializer(), msg)
                update { it.copy(sceneRun = run) }
                if (run.failed) onMessage("${pc.name}: scene stopped at \"${run.label}\". ${run.message ?: ""}".trim())
                if (!run.isRunning) {
                    scope.launch {
                        delay(4_000)
                        update { s -> if (s.sceneRun?.runId == run.runId) s.copy(sceneRun = null) else s }
                    }
                }
            }

            "stats" -> update { it.copy(stats = AppJson.decodeFromJsonElement(Stats.serializer(), msg)) }

            "config_changed" -> send("get_state", quiet = true)

            "screenshot" -> {
                val b64 = msg.str("jpeg") ?: return
                val bytes = runCatching { Base64.getDecoder().decode(b64) }.getOrNull() ?: return
                val bitmap = BitmapFactory.decodeByteArray(bytes, 0, bytes.size)
                val displays = msg["displays"]?.let { AppJson.decodeFromJsonElement(ListSerializer(DisplayInfo.serializer()), it) } ?: emptyList()
                onScreenshot(pc.id, ScreenshotFrame(
                    display = msg.int("display") ?: 0,
                    bitmap = bitmap,
                    width = msg.int("width") ?: 0,
                    height = msg.int("height") ?: 0,
                    displays = displays,
                    error = if (bitmap == null) "Could not decode the image" else null,
                ))
            }

            // The screen can't be read right now (PC locked, signed out): keep the last frame and say why.
            "stream_error" -> onScreenshot(pc.id, ScreenshotFrame(display = msg.int("display") ?: 0, error = msg.str("message") ?: "Screen unavailable", live = true))

            "error" -> {
                val code = msg.str("code")
                val text = msg.str("message") ?: "Error"
                if (msg.str("id") == STREAM_ID) {
                    onScreenshot(pc.id, ScreenshotFrame(error = text, live = true))
                    return
                }
                if (code == "busy") return
                if (code == "capture_failed" || code == "unsupported") {
                    onScreenshot(pc.id, ScreenshotFrame(error = text))
                    return
                }
                if (code == "unauthorized") {
                    update { it.copy(status = LinkStatus.UNAUTHORIZED, error = text) }
                    synchronized(lock) { socket?.close(1000, null); socket = null }
                } else {
                    onMessage("${pc.name}: $text")
                }
            }
        }
    }

    private fun update(transform: (AgentLiveState) -> AgentLiveState) {
        val next = synchronized(lock) {
            state = transform(state)
            state
        }
        onState(pc.id, next)
    }

    private fun JsonObject.str(key: String): String? = this[key]?.jsonPrimitive?.contentOrNull
    private fun JsonObject.int(key: String): Int? = str(key)?.toIntOrNull()

    private companion object {
        const val TAG = "AgentConnection"
        const val HEADER = 12
        const val STREAM_ID = "stream"
    }
}
