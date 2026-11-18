package com.movingsurface.showcontrol.data

import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import kotlinx.coroutines.withTimeoutOrNull

/**
 * Runs phone-side [ShowScene]s step by step across several PCs. One scene per room at a time:
 * starting another scene in the same room cancels the current one, so a mis-tap is fixed by
 * tapping the right tile.
 */
class ShowSceneRunner(
    private val scope: CoroutineScope,
    private val connections: ConnectionManager,
    private val settings: () -> AppSettings?,
) {
    private val _runs = MutableStateFlow<Map<String, ShowRun>>(emptyMap())
    /** Keyed by scene id. */
    val runs: StateFlow<Map<String, ShowRun>> = _runs.asStateFlow()

    private val jobs = mutableMapOf<String, Pair<String, Job>>() // roomId -> (sceneId, job)

    @Synchronized
    fun run(scene: ShowScene) {
        jobs[scene.roomId]?.let { (sceneId, job) ->
            job.cancel()
            _runs.update { m -> m[sceneId]?.let { m + (sceneId to it.copy(state = ShowRun.State.CANCELLED, message = "Replaced by ${scene.name}")) } ?: m }
        }
        val job = scope.launch { execute(scene) }
        jobs[scene.roomId] = scene.id to job
    }

    @Synchronized
    fun cancel(roomId: String) {
        jobs.remove(roomId)?.let { (sceneId, job) ->
            job.cancel()
            publish(_runs.value[sceneId]?.copy(state = ShowRun.State.CANCELLED, message = "Cancelled") ?: return)
            clearLater(sceneId)
        }
    }

    private suspend fun execute(scene: ShowScene) {
        val total = scene.steps.size
        if (total == 0) return
        for ((i, step) in scene.steps.withIndex()) {
            val label = describe(step, settings())
            publish(ShowRun(scene.id, scene.roomId, i + 1, total, label, ShowRun.State.RUNNING))
            val error = try {
                runStep(step)
            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                e.message ?: "Step failed"
            }
            if (error != null) {
                publish(ShowRun(scene.id, scene.roomId, i + 1, total, label, ShowRun.State.FAILED, error))
                connections.notify("${scene.name} stopped at \"$label\": $error")
                clearLater(scene.id, 8_000)
                return
            }
        }
        publish(ShowRun(scene.id, scene.roomId, total, total, "Done", ShowRun.State.DONE))
        clearLater(scene.id)
    }

    /** Returns null on success, or a short reason. */
    private suspend fun runStep(step: ShowStep): String? {
        if (step.type == StepType.WAIT) {
            delay((step.seconds.coerceIn(0.0, 3600.0) * 1000).toLong())
            return null
        }
        val pcId = step.pcId ?: return "No PC chosen"
        val pc = settings()?.pcs?.firstOrNull { it.id == pcId } ?: return "PC was removed"

        if (step.type == StepType.WAKE_PC) {
            if (connections.states.value[pcId]?.online == true) return null
            if (pc.hubAgentId != null) {
                // Remote PC: the hub asks another PC at that venue to send the wake signal.
                val hub = settings()?.hub ?: return "Sign in to the hub to wake ${pc.name}"
                try { HubClient.wake(connections.http, hub, pc.hubAgentId) } catch (e: HubException) { return e.message }
                if (!step.waitUntilReady) return null
                return if (await(pcId, 240_000) { it?.online == true }) null else "${pc.name} did not come online within 4 min"
            }
            val mac = pc.mac ?: return "${pc.name} has no MAC address yet (connect once while it's on)"
            WakeOnLan.send(mac)
            // Also ask any online PC in the room to send it, in case the phone's broadcast is filtered.
            settings()?.pcsIn(pc.roomId)?.forEach { other ->
                if (other.id != pcId && connections.states.value[other.id]?.online == true) connections.get(other.id)?.wake(mac)
            }
            if (!step.waitUntilReady) return null
            return if (await(pcId, 240_000) { it?.online == true }) null else "${pc.name} did not come online within 4 min"
        }

        if (!await(pcId, 8_000) { it?.online == true }) return "${pc.name} is offline"
        val conn = connections.get(pcId) ?: return "${pc.name} is not connected"
        val target = step.targetId

        return when (step.type) {
            StepType.START_APP -> {
                target ?: return "No app chosen"
                if (appState(pcId, target) == null) return "App not found on ${pc.name}"
                if (appState(pcId, target) != AppState.RUNNING) conn.startApp(target)
                if (!step.waitUntilReady) null
                else waitForApp(pcId, target, AppState.RUNNING, 90_000, failOnCrash = true)
            }
            StepType.STOP_APP -> {
                target ?: return "No app chosen"
                conn.stopApp(target)
                if (!step.waitUntilReady) null else waitForApp(pcId, target, AppState.STOPPED, 45_000)
            }
            StepType.RESTART_APP -> {
                target ?: return "No app chosen"
                conn.restartApp(target)
                if (!step.waitUntilReady) null
                else {
                    // Give the stop half a moment to register before waiting for "running".
                    await(pcId, 5_000) { s -> s?.apps?.firstOrNull { it.id == target }?.appState != AppState.RUNNING }
                    waitForApp(pcId, target, AppState.RUNNING, 90_000, failOnCrash = true)
                }
            }
            StepType.RUN_PC_SCENE -> {
                target ?: return "No scene chosen"
                val before = connections.states.value[pcId]?.sceneRun?.runId
                conn.runScene(target)
                if (!step.waitUntilReady) return null
                var result: String? = "PC scene did not finish within 10 min"
                await(pcId, 600_000) { s ->
                    val run = s?.sceneRun
                    when {
                        s?.online != true -> { result = "${pc.name} went offline"; true }
                        run == null || run.sceneId != target || run.runId == before || run.isRunning -> false
                        run.failed -> { result = run.message ?: "PC scene failed"; true }
                        else -> { result = null; true }
                    }
                }
                result
            }
            StepType.STOP_ALL_APPS -> {
                conn.stopAll()
                if (!step.waitUntilReady) null
                else if (await(pcId, 60_000) { s -> s?.apps?.none { it.appState == AppState.RUNNING || it.appState == AppState.STOPPING } == true }) null
                else "Apps on ${pc.name} did not all close"
            }
            StepType.WAKE_PC, StepType.WAIT -> null
        }
    }

    private fun appState(pcId: String, appId: String): AppState? =
        connections.states.value[pcId]?.apps?.firstOrNull { it.id == appId }?.appState

    private suspend fun waitForApp(pcId: String, appId: String, want: AppState, timeoutMs: Long, failOnCrash: Boolean = false): String? {
        var crashed: String? = null
        val ok = await(pcId, timeoutMs) { s ->
            val app = s?.apps?.firstOrNull { it.id == appId } ?: return@await false
            if (failOnCrash && app.appState == AppState.CRASHED) { crashed = app.message ?: "${app.name} crashed"; true }
            else app.appState == want
        }
        return crashed ?: if (ok) null else "Timed out waiting for $appId to be ${want.name.lowercase()}"
    }

    private suspend fun await(pcId: String, timeoutMs: Long, predicate: (AgentLiveState?) -> Boolean): Boolean =
        withTimeoutOrNull(timeoutMs) { connections.states.first { predicate(it[pcId]) } } != null

    private fun publish(run: ShowRun) = _runs.update { it + (run.sceneId to run) }

    private fun clearLater(sceneId: String, afterMs: Long = 4_000) {
        scope.launch {
            delay(afterMs)
            _runs.update { m -> if (m[sceneId]?.isRunning == false) m - sceneId else m }
        }
    }

    companion object {
        fun describe(step: ShowStep, settings: AppSettings?): String {
            val pcName = settings?.pcs?.firstOrNull { it.id == step.pcId }?.name ?: "PC"
            val target = step.targetName ?: step.targetId ?: "?"
            return when (step.type) {
                StepType.START_APP -> "Start $target"
                StepType.STOP_APP -> "Close $target"
                StepType.RESTART_APP -> "Restart $target"
                StepType.RUN_PC_SCENE -> "Run $target"
                StepType.STOP_ALL_APPS -> "Close all on $pcName"
                StepType.WAKE_PC -> "Wake $pcName"
                StepType.WAIT -> "Wait ${formatSeconds(step.seconds)}"
            }
        }

        fun formatSeconds(s: Double): String = if (s % 1.0 == 0.0) "${s.toInt()} s" else "%.1f s".format(s)
    }
}
