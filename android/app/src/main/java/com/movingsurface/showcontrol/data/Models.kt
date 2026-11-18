package com.movingsurface.showcontrol.data

import android.graphics.Bitmap
import java.util.UUID
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json

val AppJson = Json {
    ignoreUnknownKeys = true
    explicitNulls = false
    encodeDefaults = true
    coerceInputValues = true
}

fun newId(): String = UUID.randomUUID().toString().replace("-", "").take(12)

// ---------- saved on the phone ----------

@Serializable
enum class ThemeMode { SYSTEM, LIGHT, DARK }

@Serializable
enum class RoomKind(val label: String) {
    PRODUCTION("Production"),
    INSTALLATION("Installation"),
    EVENT("Event"),
    LAB("Test lab"),
}

@Serializable
data class Room(
    val id: String = newId(),
    val name: String,
    val kind: RoomKind = RoomKind.PRODUCTION,
)

@Serializable
data class PcEndpoint(
    val id: String = newId(),
    val roomId: String,
    val name: String,
    val host: String,
    val port: Int = 47800,
    val token: String,
    val agentId: String? = null,
    /** Learned from the agent while online; used for Wake-on-LAN when it's off. */
    val mac: String? = null,
    /**
     * Set for PCs reached through a Show Control hub instead of the local network. host/port then
     * hold the hub's address for display, and the connection uses the signed-in [HubAccount].
     */
    val hubAgentId: String? = null,
) {
    val viaHub: Boolean get() = hubAgentId != null
}

/** The hub this phone is signed in to (remote control over the internet). */
@Serializable
data class HubAccount(
    /** Base address, e.g. https://hub.movingsurface.com.ng or http://192.168.1.10:47900 */
    val url: String,
    val token: String,
    val username: String = "",
    val displayName: String = "",
    /** admin | operator | viewer */
    val role: String = "operator",
    val workspace: String = "",
)

/** Live view frame rate and size. Smooth suits the venue network; Data saver suits mobile data. */
@Serializable
enum class LiveViewQuality(val label: String, val fps: Int, val width: Int, val fullWidth: Int, val quality: Int) {
    SMOOTH("Smooth", 30, 960, 1600, 65),
    BALANCED("Balanced", 15, 720, 1280, 60),
    SAVER("Data saver", 5, 480, 960, 50),
}

/** What the phone tells you about, and whether it keeps watching when the app is closed. */
@Serializable
data class AlertSettings(
    /** An app crashed (and the watchdog may be restarting it). */
    val crashes: Boolean = true,
    /** A PC that was online can't be reached for 30 s. */
    val offline: Boolean = false,
    /** Keep connections open in the background (a small persistent notification) so alerts arrive with the app closed. */
    val background: Boolean = false,
)

/** How much effort a tile takes to fire. */
@Serializable
enum class SafetyMode(val label: String) { TAP("Tap"), CONFIRM("Confirm"), HOLD("Hold") }

@Serializable
enum class StepType(val label: String, val needsPc: Boolean, val needsApp: Boolean, val needsScene: Boolean, val canWait: Boolean) {
    START_APP("Start app", true, true, false, true),
    STOP_APP("Close app", true, true, false, true),
    RESTART_APP("Restart app", true, true, false, true),
    RUN_PC_SCENE("Run PC scene", true, false, true, true),
    STOP_ALL_APPS("Close all apps on a PC", true, false, false, true),
    WAKE_PC("Wake PC", true, false, false, true),
    WAIT("Wait", false, false, false, false),
}

@Serializable
data class ShowStep(
    val id: String = newId(),
    val type: StepType,
    val pcId: String? = null,
    /** appId or sceneId on that PC. */
    val targetId: String? = null,
    /** Display name cached when the step was made, so it reads well while the PC is offline. */
    val targetName: String? = null,
    val seconds: Double = 0.0,
    /** Wait for the app to be running / the PC scene to finish / the PC to come online. */
    val waitUntilReady: Boolean = true,
)

/** A one-tap scene that can span several PCs. Lives on the phone, runs from the phone. */
@Serializable
data class ShowScene(
    val id: String = newId(),
    val roomId: String,
    val name: String,
    val color: String = "amber",
    val icon: String = "play",
    val safety: SafetyMode = SafetyMode.TAP,
    val steps: List<ShowStep> = emptyList(),
)

@Serializable
data class AppSettings(
    val rooms: List<Room> = emptyList(),
    val pcs: List<PcEndpoint> = emptyList(),
    val scenes: List<ShowScene> = emptyList(),
    val theme: ThemeMode = ThemeMode.SYSTEM,
    val accent: String = "indigo",
    val brandName: String = "Moving Surface",
    val deviceName: String = "",
    /** 0 = no logo; otherwise changes whenever the logo file (filesDir/brand_logo.png) is replaced. */
    val logoVersion: Long = 0,
    val hub: HubAccount? = null,
    val liveView: LiveViewQuality = LiveViewQuality.SMOOTH,
    val alerts: AlertSettings = AlertSettings(),
    /** Set once the notification permission has been asked for, so we don't nag. */
    val askedNotifications: Boolean = false,
    /** Bumped by one-off settings upgrades (see SettingsRepository.migrate). */
    val settingsVersion: Int = 0,
) {
    fun pcsIn(roomId: String) = pcs.filter { it.roomId == roomId }
    fun scenesIn(roomId: String) = scenes.filter { it.roomId == roomId }

    /**
     * Whether this phone may control a PC. The PC says so when it connects (role in its welcome); before
     * that, or with an older agent, a hub PC follows the hub account's role and a paired PC can control.
     */
    fun canControl(pcId: String, live: AgentLiveState?): Boolean {
        live?.role?.let { return it != "viewer" }
        val pc = pcs.firstOrNull { it.id == pcId } ?: return true
        return !(pc.viaHub && hub?.role == "viewer")
    }
}

// ---------- received from agents (see protocol/PROTOCOL.md) ----------

@Serializable
data class DisplayInfo(
    val index: Int = 0,
    val name: String = "",
    val width: Int = 0,
    val height: Int = 0,
    val primary: Boolean = false,
)

@Serializable
data class AgentInfo(
    val agentId: String = "",
    val name: String = "",
    val hostname: String = "",
    val version: String = "",
    val ips: List<String> = emptyList(),
    val macs: List<String> = emptyList(),
    val allowPower: Boolean = false,
    val canScreenshot: Boolean = false,
    /** Agent 1.3+: pushes a live stream of binary JPEG frames (stream_start). */
    val canStream: Boolean = false,
    val displays: List<DisplayInfo> = emptyList(),
)

enum class AppState { STOPPED, STARTING, RUNNING, STOPPING, CRASHED;
    companion object {
        fun parse(s: String?): AppState = entries.firstOrNull { it.name.equals(s, ignoreCase = true) } ?: STOPPED
    }
}

@Serializable
data class AppInfo(
    val id: String,
    val name: String,
    val color: String? = null,
    val icon: String? = null,
    val state: String = "stopped",
    val pid: Int? = null,
    val since: String? = null,
    val message: String? = null,
    val watchdog: Boolean = false,
) {
    val appState: AppState get() = AppState.parse(state)
}

@Serializable
data class SceneInfo(
    val id: String,
    val name: String,
    val color: String? = null,
    val icon: String? = null,
    val group: String? = null,
    val confirm: Boolean = false,
    val steps: Int = 0,
)

@Serializable
data class Stats(
    val cpu: Double = 0.0,
    val ramUsedMb: Long = 0,
    val ramTotalMb: Long = 0,
    val uptimeSec: Long = 0,
)

@Serializable
data class SceneRun(
    val sceneId: String,
    val runId: String = "",
    val step: Int = 0,
    val total: Int = 0,
    val label: String = "",
    val state: String = "running",
    val message: String? = null,
) {
    val isRunning get() = state == "running"
    val failed get() = state == "failed"
}

/** Latest screen grab from a PC, or why there isn't one. */
data class ScreenshotFrame(
    val display: Int = 0,
    val bitmap: Bitmap? = null,
    val width: Int = 0,
    val height: Int = 0,
    val displays: List<DisplayInfo> = emptyList(),
    val at: Long = System.currentTimeMillis(),
    val error: String? = null,
    /** True for frames from a live stream (as opposed to a one-off screenshot). */
    val live: Boolean = false,
    val seq: Long = 0,
)

enum class LinkStatus { CONNECTING, ONLINE, OFFLINE, UNAUTHORIZED }

data class AgentLiveState(
    val status: LinkStatus = LinkStatus.CONNECTING,
    val agent: AgentInfo? = null,
    val apps: List<AppInfo> = emptyList(),
    val scenes: List<SceneInfo> = emptyList(),
    val stats: Stats? = null,
    val sceneRun: SceneRun? = null,
    val error: String? = null,
    /** admin | operator | viewer, as the PC sees this phone (null before it says, or with an old agent). */
    val role: String? = null,
) {
    val online get() = status == LinkStatus.ONLINE
    val crashedCount get() = apps.count { it.appState == AppState.CRASHED }
    val runningCount get() = apps.count { it.appState == AppState.RUNNING }
}

/** Progress of a phone-side [ShowScene] run. */
data class ShowRun(
    val sceneId: String,
    val roomId: String,
    val step: Int,
    val total: Int,
    val label: String,
    val state: State,
    val message: String? = null,
) {
    enum class State { RUNNING, DONE, FAILED, CANCELLED }
    val isRunning get() = state == State.RUNNING
}

/** Something worth telling the operator about, even when they're looking at another screen. */
sealed class PcAlert {
    abstract val pcId: String
    abstract val pcName: String

    data class AppCrashed(override val pcId: String, override val pcName: String, val appId: String, val appName: String, val message: String?) : PcAlert()
    data class AppRecovered(override val pcId: String, override val pcName: String, val appId: String, val appName: String) : PcAlert()
    data class PcOffline(override val pcId: String, override val pcName: String) : PcAlert()
    data class PcOnline(override val pcId: String, override val pcName: String) : PcAlert()
}
