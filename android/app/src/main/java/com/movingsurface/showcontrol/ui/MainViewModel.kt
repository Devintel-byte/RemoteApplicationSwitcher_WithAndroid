package com.movingsurface.showcontrol.ui

import android.app.Application
import android.net.Uri
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import com.movingsurface.showcontrol.ShowControlApp
import com.movingsurface.showcontrol.data.AgentLiveState
import com.movingsurface.showcontrol.data.AlertSettings
import com.movingsurface.showcontrol.data.AppSettings
import com.movingsurface.showcontrol.data.BrandLogo
import com.movingsurface.showcontrol.data.DiscoveredAgent
import com.movingsurface.showcontrol.data.HubAccount
import com.movingsurface.showcontrol.data.HubAgent
import com.movingsurface.showcontrol.data.HubClient
import com.movingsurface.showcontrol.data.HubException
import com.movingsurface.showcontrol.data.HubSharedRoom
import com.movingsurface.showcontrol.data.HubWorkspace
import com.movingsurface.showcontrol.data.LiveViewQuality
import com.movingsurface.showcontrol.data.PairException
import com.movingsurface.showcontrol.data.PcEndpoint
import com.movingsurface.showcontrol.data.PowerAction
import com.movingsurface.showcontrol.data.PowerPlan
import com.movingsurface.showcontrol.data.PowerPlanner
import com.movingsurface.showcontrol.data.Room
import com.movingsurface.showcontrol.data.RoomImport
import com.movingsurface.showcontrol.data.RoomKind
import com.movingsurface.showcontrol.data.RoomPack
import com.movingsurface.showcontrol.data.RoomShare
import com.movingsurface.showcontrol.data.ScreenshotFrame
import com.movingsurface.showcontrol.data.ShowRun
import com.movingsurface.showcontrol.data.ShowScene
import com.movingsurface.showcontrol.data.StreamRequest
import com.movingsurface.showcontrol.data.ThemeMode
import com.movingsurface.showcontrol.data.WakeOnLan
import com.movingsurface.showcontrol.data.discoverAgents
import com.movingsurface.showcontrol.data.pairWithAgent
import com.movingsurface.showcontrol.ui.theme.Accents
import okhttp3.HttpUrl.Companion.toHttpUrlOrNull
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.stateIn
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch

data class DiscoveryUi(val scanning: Boolean = false, val results: List<DiscoveredAgent> = emptyList(), val scannedOnce: Boolean = false)
data class PairUi(val busy: Boolean = false, val error: String? = null)
/** The import screen: a decoded pack waiting for confirmation, and the hub's shared rooms. */
data class ImportUi(
    val preview: RoomImport? = null,
    val pack: RoomPack? = null,
    val error: String? = null,
    val busy: Boolean = false,
    val hubRooms: List<HubSharedRoom> = emptyList(),
    val loadingHub: Boolean = false,
    val hubError: String? = null,
)

data class HubUi(
    val busy: Boolean = false,
    val error: String? = null,
    val agents: List<HubAgent> = emptyList(),
    val loadingAgents: Boolean = false,
    val agentsError: String? = null,
)

class MainViewModel(application: Application) : AndroidViewModel(application) {

    private val app = application as ShowControlApp
    private val repo = app.settings
    private val connections = app.connections

    /** null until the first read from storage, so the UI never flashes an empty state. */
    val settings: StateFlow<AppSettings?> = repo.settings.stateIn(viewModelScope, SharingStarted.Eagerly, null)
    val live: StateFlow<Map<String, AgentLiveState>> = connections.states
    val messages: SharedFlow<String> = connections.messages
    val showRuns: StateFlow<Map<String, ShowRun>> = app.runner.runs
    val screenshots: StateFlow<Map<String, ScreenshotFrame>> = connections.screenshots

    private val _discovery = MutableStateFlow(DiscoveryUi())
    val discovery = _discovery.asStateFlow()

    private val _pair = MutableStateFlow(PairUi())
    val pair = _pair.asStateFlow()

    private val _hub = MutableStateFlow(HubUi())
    val hub = _hub.asStateFlow()

    private val _import = MutableStateFlow(ImportUi())
    val importUi = _import.asStateFlow()

    /** Set by a notification tap (open that PC) or a shared room code arriving from another app. */
    private val _openPc = MutableStateFlow<String?>(null)
    val openPc = _openPc.asStateFlow()
    private val _incomingCode = MutableStateFlow<String?>(null)
    val incomingCode = _incomingCode.asStateFlow()

    fun requestOpenPc(pcId: String?) { _openPc.value = pcId }
    fun consumeOpenPc() { _openPc.value = null }
    fun receiveSharedText(text: String?) { if (RoomShare.containsCode(text)) _incomingCode.value = text }
    fun consumeIncomingCode() { _incomingCode.value = null }

    private val context get() = getApplication<Application>()

    private fun edit(transform: (AppSettings) -> AppSettings) {
        viewModelScope.launch { repo.update(transform) }
    }

    // ---------- appearance & branding ----------

    fun setTheme(mode: ThemeMode) = edit { it.copy(theme = mode) }
    fun toggleTheme(currentlyDark: Boolean) = setTheme(if (currentlyDark) ThemeMode.LIGHT else ThemeMode.DARK)
    fun setAccent(key: String) = edit { it.copy(accent = key) }
    fun setBrandName(name: String) = edit { it.copy(brandName = name) }
    fun setDeviceName(name: String) = edit { it.copy(deviceName = name) }
    fun setLiveView(quality: LiveViewQuality) = edit { it.copy(liveView = quality) }
    fun setAlerts(alerts: AlertSettings) = edit { it.copy(alerts = alerts) }
    fun markAskedNotifications() = edit { it.copy(askedNotifications = true) }
    fun notificationsPermitted(): Boolean = app.notifier.permitted()

    fun setLogoFromUri(uri: Uri) {
        viewModelScope.launch {
            if (BrandLogo.saveFromUri(context, uri)) repo.update { it.copy(logoVersion = System.currentTimeMillis()) }
            else connections.notify("That image couldn't be read. Try a PNG or JPG.")
        }
    }

    fun removeLogo() {
        BrandLogo.delete(context)
        edit { it.copy(logoVersion = 0) }
    }

    // ---------- hub (remote control over the internet) ----------

    private fun deviceName() = settings.value?.deviceName?.ifBlank { null } ?: ShowControlApp.defaultDeviceName()

    fun hubSignIn(address: String, username: String, password: String, onDone: () -> Unit) {
        if (_hub.value.busy) return
        val url = HubClient.normalizeUrl(address)
        if (url.toHttpUrlOrNull() == null) { _hub.update { it.copy(error = "That doesn't look like a hub address") }; return }
        _hub.update { it.copy(busy = true, error = null) }
        viewModelScope.launch {
            try {
                val login = HubClient.login(connections.http, url, username, password, deviceName())
                val account = HubAccount(
                    url = url, token = login.token, username = login.user.username,
                    displayName = login.user.displayName, role = login.user.role, workspace = login.workspace.name,
                )
                repo.update { it.copy(hub = account) }
                applyBranding(account, login.workspace)
                _hub.update { HubUi() }
                onDone()
                refreshHubAgents()
            } catch (e: HubException) {
                _hub.update { it.copy(busy = false, error = e.message) }
            }
        }
    }

    /** Signs out and forgets the hub's PCs on this phone (they can't be reached without the hub). */
    fun hubSignOut() {
        val account = settings.value?.hub ?: return
        viewModelScope.launch {
            HubClient.logout(connections.http, account)
            repo.update { s -> s.copy(hub = null, pcs = s.pcs.filterNot { it.viaHub }) }
            _hub.value = HubUi()
        }
    }

    fun refreshHubAgents() {
        val account = settings.value?.hub ?: return
        _hub.update { it.copy(loadingAgents = true, agentsError = null) }
        viewModelScope.launch {
            try {
                val agents = HubClient.agents(connections.http, account)
                _hub.update { it.copy(loadingAgents = false, agents = agents) }
            } catch (e: HubException) {
                _hub.update { it.copy(loadingAgents = false, agentsError = e.message) }
            }
        }
    }

    /** Re-reads name, role and branding from the hub (after the admin changes the logo, say). */
    fun refreshHubBranding() {
        val account = settings.value?.hub ?: return
        viewModelScope.launch {
            try {
                val (user, workspace) = HubClient.me(connections.http, account)
                val updated = account.copy(displayName = user.displayName, role = user.role, workspace = workspace.name)
                repo.update { it.copy(hub = updated) }
                applyBranding(updated, workspace)
                connections.notify("Branding updated from ${workspace.name}")
            } catch (e: HubException) {
                connections.notify(e.message ?: "Couldn't reach the hub")
            }
        }
    }

    private suspend fun applyBranding(account: HubAccount, workspace: HubWorkspace) {
        val logo = HubClient.downloadLogo(connections.http, account, workspace.logoUrl)
        val hasLogo = logo != null && BrandLogo.save(context, logo)
        if (workspace.logoUrl == null) BrandLogo.delete(context)
        repo.update { s ->
            s.copy(
                brandName = workspace.name,
                accent = if (Accents.any { it.key == workspace.accent }) workspace.accent else s.accent,
                logoVersion = when {
                    hasLogo -> System.currentTimeMillis()
                    workspace.logoUrl == null -> 0
                    else -> s.logoVersion // logo couldn't be shown (e.g. SVG): keep what we had
                },
            )
        }
    }

    fun addHubPc(roomId: String, agent: HubAgent) {
        val account = settings.value?.hub ?: return
        val (host, port) = HubClient.hostAndPort(account.url)
        edit { s ->
            if (s.pcs.any { it.hubAgentId == agent.agentId && it.roomId == roomId }) s
            else s.copy(pcs = s.pcs + PcEndpoint(roomId = roomId, name = agent.name, host = host, port = port, token = "", hubAgentId = agent.agentId))
        }
    }

    // ---------- rooms & PCs ----------

    fun addRoom(name: String, kind: RoomKind) = edit { it.copy(rooms = it.rooms + Room(name = name.trim(), kind = kind)) }

    fun updateRoom(room: Room) = edit { s -> s.copy(rooms = s.rooms.map { if (it.id == room.id) room else it }) }

    fun deleteRoom(roomId: String) = edit { s ->
        s.copy(
            rooms = s.rooms.filterNot { it.id == roomId },
            pcs = s.pcs.filterNot { it.roomId == roomId },
            scenes = s.scenes.filterNot { it.roomId == roomId },
        )
    }

    fun renamePc(pcId: String, name: String) = edit { s ->
        s.copy(pcs = s.pcs.map { if (it.id == pcId) it.copy(name = name.trim()) else it })
    }

    fun deletePc(pcId: String) = edit { s -> s.copy(pcs = s.pcs.filterNot { it.id == pcId }) }

    // ---------- show scenes (tiles) ----------

    fun saveShowScene(scene: ShowScene) = edit { s ->
        if (s.scenes.any { it.id == scene.id }) s.copy(scenes = s.scenes.map { if (it.id == scene.id) scene else it })
        else s.copy(scenes = s.scenes + scene)
    }

    fun deleteShowScene(sceneId: String) = edit { s -> s.copy(scenes = s.scenes.filterNot { it.id == sceneId }) }

    /** Moves a tile one place earlier (-1) or later (+1) among its room's tiles. */
    fun moveShowScene(sceneId: String, delta: Int) = edit { s ->
        val list = s.scenes.toMutableList()
        val i = list.indexOfFirst { it.id == sceneId }
        if (i < 0) return@edit s
        val room = list[i].roomId
        val sameRoom = list.indices.filter { list[it].roomId == room }
        val pos = sameRoom.indexOf(i)
        val swapWith = sameRoom.getOrNull(pos + delta) ?: return@edit s
        list[i] = list[swapWith].also { list[swapWith] = list[i] }
        s.copy(scenes = list)
    }

    fun runShowScene(scene: ShowScene) = app.runner.run(scene)
    fun cancelShowScenes(roomId: String) = app.runner.cancel(roomId)

    // ---------- wake & screenshots ----------

    fun wakePc(pcId: String) {
        val s = settings.value ?: return
        val pc = s.pcs.firstOrNull { it.id == pcId } ?: return
        if (pc.hubAgentId != null) {
            val account = s.hub ?: return
            viewModelScope.launch {
                try {
                    HubClient.wake(connections.http, account, pc.hubAgentId)
                    connections.notify("Wake signal sent to ${pc.name} through the hub")
                } catch (e: HubException) {
                    connections.notify(e.message ?: "Couldn't wake ${pc.name}")
                }
            }
            return
        }
        val mac = pc.mac
        if (mac == null) {
            connections.notify("${pc.name} has no MAC address yet. Connect to it once while it's on.")
            return
        }
        viewModelScope.launch {
            runCatching { WakeOnLan.send(mac) }
            s.pcsIn(pc.roomId).forEach { other ->
                if (other.id != pcId && live.value[other.id]?.online == true) connections.get(other.id)?.wake(mac)
            }
            connections.notify("Wake signal sent to ${pc.name}")
        }
    }

    fun wakeRoom(roomId: String) {
        val s = settings.value ?: return
        s.pcsIn(roomId).filter { it.viaHub && live.value[it.id]?.online != true }.forEach { wakePc(it.id) }
        val targets = s.pcsIn(roomId).filter { !it.viaHub && live.value[it.id]?.online != true && it.mac != null }
        if (targets.isEmpty()) {
            if (s.pcsIn(roomId).none { it.viaHub }) connections.notify("No offline PCs with a known MAC address")
            return
        }
        viewModelScope.launch {
            targets.forEach { pc ->
                runCatching { WakeOnLan.send(pc.mac!!) }
                s.pcsIn(roomId).forEach { other ->
                    if (live.value[other.id]?.online == true) connections.get(other.id)?.wake(pc.mac!!)
                }
            }
            connections.notify("Wake signal sent to ${targets.size} ${if (targets.size == 1) "PC" else "PCs"}")
        }
    }

    // ---------- whole-site / whole-room power ----------

    /** What a power action would do right now (for the confirmation). roomId null = every PC on this phone. */
    fun powerPlan(action: PowerAction, roomId: String? = null): PowerPlan =
        PowerPlanner.plan(settings.value ?: AppSettings(), live.value, action, roomId)

    fun runPower(action: PowerAction, roomId: String? = null) {
        val s = settings.value ?: return
        val plan = PowerPlanner.plan(s, live.value, action, roomId)
        val targets = s.pcs.filter { it.id in plan.targetIds }
        when (action) {
            PowerAction.WAKE -> viewModelScope.launch {
                // Hub PCs: the hub asks a PC at their venue to wake them. LAN PCs: broadcast from the phone, and
                // ask every online PC to repeat it (reaches PCs on other switches/VLANs).
                val relays = s.pcs.filter { !it.viaHub && live.value[it.id]?.online == true }.distinctBy { it.agentId ?: it.id }
                var failed = 0
                for (pc in targets) {
                    if (pc.hubAgentId != null) {
                        val account = s.hub
                        if (account == null) { failed++; continue }
                        try { HubClient.wake(connections.http, account, pc.hubAgentId) } catch (e: HubException) { failed++ }
                    } else {
                        val mac = pc.mac ?: continue
                        runCatching { WakeOnLan.send(mac) }
                        relays.forEach { r -> connections.get(r.id)?.wake(mac) }
                    }
                }
                val sent = targets.size - failed
                connections.notify("Wake signal sent to $sent ${if (sent == 1) "PC" else "PCs"}" +
                    (if (failed > 0) " · $failed failed through the hub" else "") + ". They take a minute or two to start.")
            }
            PowerAction.RESTART, PowerAction.SHUTDOWN -> {
                val word = if (action == PowerAction.RESTART) "restart" else "shutdown"
                targets.forEach { connections.get(it.id)?.power(word) }
                val skipped = plan.noPower.size + plan.offline.size + plan.watchOnly.size
                connections.notify(
                    (if (action == PowerAction.RESTART) "Restarting" else "Shutting down") + " ${targets.size} ${if (targets.size == 1) "PC" else "PCs"}" +
                        (if (skipped > 0) " · $skipped skipped" else ""),
                )
            }
        }
    }

    fun requestScreenshot(pcId: String, display: Int, maxWidth: Int = 720) {
        connections.get(pcId)?.requestScreenshot(display, maxWidth)
    }

    /** True when the PC pushes a live stream; otherwise the live view falls back to snapshots. */
    fun canStream(pcId: String): Boolean = connections.get(pcId)?.canStream == true

    fun startLiveView(pcId: String, display: Int, fullscreen: Boolean) {
        val q = settings.value?.liveView ?: LiveViewQuality.SMOOTH
        connections.startStream(pcId, StreamRequest(display, if (fullscreen) q.fullWidth else q.width, q.fps, q.quality))
    }

    fun stopLiveView(pcId: String) = connections.stopStream(pcId)

    // ---------- sharing a room's tiles ----------

    /** A code to send to another phone (WhatsApp, email…). Contains no pairing keys or passwords. */
    fun shareCode(roomId: String): String = RoomShare.encode(RoomShare.build(settings.value ?: AppSettings(), roomId, deviceName()))

    fun publishRoom(roomId: String) {
        val s = settings.value ?: return
        val account = s.hub ?: return
        val pack = RoomShare.build(s, roomId, deviceName())
        viewModelScope.launch {
            try {
                HubClient.publishRoom(connections.http, account, pack)
                connections.notify("${pack.room.name} is on ${account.workspace.ifBlank { "the hub" }}. Other phones can import it from Rooms → Import.")
            } catch (e: HubException) {
                connections.notify(e.message ?: "Couldn't publish the room")
            }
        }
    }

    fun previewCode(text: String) {
        val s = settings.value ?: return
        try {
            val pack = RoomShare.decode(text)
            _import.update { it.copy(pack = pack, preview = RoomShare.merge(s, pack), error = null) }
        } catch (e: IllegalArgumentException) {
            _import.update { it.copy(pack = null, preview = null, error = e.message) }
        }
    }

    fun loadHubRooms() {
        val account = settings.value?.hub ?: return
        _import.update { it.copy(loadingHub = true, hubError = null) }
        viewModelScope.launch {
            try {
                val rooms = HubClient.sharedRooms(connections.http, account)
                _import.update { it.copy(loadingHub = false, hubRooms = rooms) }
            } catch (e: HubException) {
                _import.update { it.copy(loadingHub = false, hubError = e.message) }
            }
        }
    }

    fun previewHubRoom(id: String) {
        val account = settings.value?.hub ?: return
        _import.update { it.copy(busy = true, error = null) }
        viewModelScope.launch {
            try {
                val pack = HubClient.sharedRoom(connections.http, account, id)
                val s = settings.value ?: AppSettings()
                _import.update { it.copy(busy = false, pack = pack, preview = RoomShare.merge(s, pack)) }
            } catch (e: Exception) {
                _import.update { it.copy(busy = false, error = e.message ?: "Couldn't fetch that room") }
            }
        }
    }

    fun clearImportPreview() = _import.update { it.copy(pack = null, preview = null, error = null) }

    /** Applies the previewed import (recomputed against the latest settings) and reports the room id. */
    fun confirmImport(onDone: (roomId: String) -> Unit) {
        val pack = _import.value.pack ?: return
        viewModelScope.launch {
            var roomId = pack.room.id
            repo.update { s -> RoomShare.merge(s, pack).also { roomId = it.roomId }.settings }
            _import.value = ImportUi(hubRooms = _import.value.hubRooms)
            onDone(roomId)
        }
    }

    // ---------- commands ----------

    fun startApp(pcId: String, appId: String) { connections.get(pcId)?.startApp(appId) }
    fun stopApp(pcId: String, appId: String, force: Boolean = false) { connections.get(pcId)?.stopApp(appId, force) }
    fun restartApp(pcId: String, appId: String) { connections.get(pcId)?.restartApp(appId) }
    fun runScene(pcId: String, sceneId: String) { connections.get(pcId)?.runScene(sceneId) }
    fun stopAllOnPc(pcId: String) { connections.get(pcId)?.stopAll() }
    fun power(pcId: String, action: String) { connections.get(pcId)?.power(action) }
    fun reconnect(pcId: String) { connections.get(pcId)?.reconnectNow() }
    fun reconnectAll() = connections.reconnectAll()
    fun notifyUser(text: String) = connections.notify(text)

    /** Stops every app (and any running show scene) on every PC this phone can reach. */
    fun stopAllEverywhere() {
        val s = settings.value ?: return
        s.rooms.forEach { app.runner.cancel(it.id) }
        s.pcs.filter { live.value[it.id]?.online == true }.distinctBy { it.hubAgentId ?: it.agentId ?: it.id }
            .forEach { pc -> connections.get(pc.id)?.stopAll() }
    }

    fun stopAllInRoom(roomId: String) {
        app.runner.cancel(roomId)
        settings.value?.pcsIn(roomId)?.forEach { pc ->
            if (live.value[pc.id]?.online == true) connections.get(pc.id)?.stopAll()
        }
    }

    // ---------- add PC ----------

    fun scan() {
        if (_discovery.value.scanning) return
        _discovery.update { it.copy(scanning = true) }
        viewModelScope.launch {
            val found = runCatching { discoverAgents() }.getOrDefault(emptyList())
            val known = settings.value?.pcs?.mapNotNull { it.agentId }?.toSet().orEmpty()
            _discovery.value = DiscoveryUi(scanning = false, results = found.filterNot { it.agentId in known }, scannedOnce = true)
        }
    }

    fun resetAddPc() {
        _discovery.value = DiscoveryUi()
        _pair.value = PairUi()
    }

    fun pairPc(roomId: String, host: String, port: Int, pin: String, displayName: String?, onDone: () -> Unit) {
        if (_pair.value.busy) return
        _pair.value = PairUi(busy = true)
        viewModelScope.launch {
            val deviceName = settings.value?.deviceName?.ifBlank { null } ?: ShowControlApp.defaultDeviceName()
            try {
                val result = pairWithAgent(connections.http, host.trim(), port, pin.trim(), deviceName)
                repo.update { s ->
                    // Same PC already in this room: replace it. In other rooms (a shared room links the same PC): new key.
                    val others = s.pcs
                        .filterNot { it.roomId == roomId && it.agentId != null && it.agentId == result.agentId }
                        .map { if (!it.viaHub && it.agentId != null && it.agentId == result.agentId) it.copy(token = result.token, host = host.trim(), port = port) else it }
                    s.copy(pcs = others + PcEndpoint(
                        roomId = roomId,
                        name = displayName?.takeIf { it.isNotBlank() } ?: result.agentName,
                        host = host.trim(),
                        port = port,
                        token = result.token,
                        agentId = result.agentId,
                    ))
                }
                _pair.value = PairUi()
                onDone()
            } catch (e: PairException) {
                _pair.value = PairUi(error = e.message)
            } catch (e: Exception) {
                _pair.value = PairUi(error = e.message ?: "Pairing failed")
            }
        }
    }

    /** Re-pair an existing PC whose token was revoked; keeps its room and name. */
    fun repairPc(pc: PcEndpoint, pin: String, onDone: () -> Unit) {
        if (_pair.value.busy) return
        if (pc.viaHub) {
            _pair.value = PairUi(error = "This PC is reached through your hub. Sign in to the hub again in Settings → Remote access.")
            return
        }
        _pair.value = PairUi(busy = true)
        viewModelScope.launch {
            val deviceName = settings.value?.deviceName?.ifBlank { null } ?: ShowControlApp.defaultDeviceName()
            try {
                val result = pairWithAgent(connections.http, pc.host, pc.port, pin.trim(), deviceName)
                repo.update { s ->
                    s.copy(pcs = s.pcs.map {
                        when {
                            it.id == pc.id -> it.copy(token = result.token, agentId = result.agentId)
                            // The same PC in other rooms (shared rooms) gets the new key too.
                            !it.viaHub && it.agentId != null && it.agentId == result.agentId -> it.copy(token = result.token)
                            else -> it
                        }
                    })
                }
                _pair.value = PairUi()
                onDone()
            } catch (e: Exception) {
                _pair.value = PairUi(error = e.message ?: "Pairing failed")
            }
        }
    }
}
