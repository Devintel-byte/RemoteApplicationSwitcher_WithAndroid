package com.movingsurface.showcontrol.data

enum class PowerAction { WAKE, RESTART, SHUTDOWN }

/**
 * What a whole-site (or whole-room) power action would do. Names are what the confirmation shows;
 * [targetIds] are the PC entries to act on. The same PC listed in several rooms counts once.
 */
data class PowerPlan(
    val action: PowerAction,
    val total: Int,
    val online: Int,
    val targetIds: List<String>,
    val targets: List<String>,
    /** Online, but "Allow restart and shut down" is off on the PC. */
    val noPower: List<String> = emptyList(),
    /** Restart / shut down only reach PCs that are on. */
    val offline: List<String> = emptyList(),
    /** Off, but no MAC address learned yet (and not reached through a hub). */
    val unreachable: List<String> = emptyList(),
    /** This phone may only watch these. */
    val watchOnly: List<String> = emptyList(),
)

object PowerPlanner {

    private fun key(pc: PcEndpoint) = pc.hubAgentId?.let { "hub:$it" } ?: pc.agentId?.let { "lan:$it" } ?: "pc:${pc.id}"

    fun plan(s: AppSettings, live: Map<String, AgentLiveState>, action: PowerAction, roomId: String? = null): PowerPlan {
        val pcs = if (roomId == null) s.pcs else s.pcsIn(roomId)
        // One entry per physical PC, preferring the entry whose connection is up.
        val reps = pcs.groupBy(::key).values.map { same -> same.firstOrNull { live[it.id]?.online == true } ?: same.first() }
        val online = reps.count { live[it.id]?.online == true }

        val targets = mutableListOf<PcEndpoint>()
        val noPower = mutableListOf<String>()
        val offline = mutableListOf<String>()
        val unreachable = mutableListOf<String>()
        val watchOnly = mutableListOf<String>()

        for (pc in reps) {
            val state = live[pc.id]
            val isOn = state?.online == true
            when (action) {
                PowerAction.WAKE -> when {
                    isOn -> {}
                    pc.viaHub && s.hub == null -> unreachable += pc.name
                    pc.viaHub && s.hub?.role == "viewer" -> watchOnly += pc.name
                    pc.viaHub || pc.mac != null -> targets += pc
                    else -> unreachable += pc.name
                }
                PowerAction.RESTART, PowerAction.SHUTDOWN -> when {
                    !isOn -> offline += pc.name
                    !s.canControl(pc.id, state) -> watchOnly += pc.name
                    state?.agent?.allowPower != true -> noPower += pc.name
                    else -> targets += pc
                }
            }
        }
        return PowerPlan(
            action = action, total = reps.size, online = online,
            targetIds = targets.map { it.id }, targets = targets.map { it.name },
            noPower = noPower, offline = offline, unreachable = unreachable, watchOnly = watchOnly,
        )
    }
}
