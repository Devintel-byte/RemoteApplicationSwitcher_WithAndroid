package com.movingsurface.showcontrol.ui.components

import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.rounded.PowerOff
import androidx.compose.material.icons.rounded.PowerSettingsNew
import androidx.compose.material.icons.rounded.RestartAlt
import androidx.compose.runtime.Composable
import com.movingsurface.showcontrol.data.PowerAction
import com.movingsurface.showcontrol.data.PowerPlan

/**
 * Turn on (Wake-on-LAN) / Restart / Shut down for a set of PCs (every PC, or one room), as menu items.
 * Each opens [PowerConfirmDialog], which says exactly which PCs it will act on and which it skips.
 */
fun powerOps(scope: String, plan: (PowerAction) -> PowerPlan, onAsk: (PowerAction) -> Unit): List<OpItem> {
    val wake = plan(PowerAction.WAKE)
    val restart = plan(PowerAction.RESTART)
    val shutdown = plan(PowerAction.SHUTDOWN)
    fun count(n: Int) = "$n ${if (n == 1) "PC" else "PCs"}"
    return listOf(
        OpItem(
            "Turn on $scope", Icons.Rounded.PowerSettingsNew,
            supporting = if (wake.targets.isEmpty()) (if (wake.total > 0 && wake.online == wake.total) "All on" else "Nothing to wake") else "Wake-on-LAN · ${count(wake.targets.size)} off",
            enabled = wake.targets.isNotEmpty(),
        ) { onAsk(PowerAction.WAKE) },
        OpItem(
            "Restart $scope", Icons.Rounded.RestartAlt,
            supporting = if (restart.targets.isEmpty()) "No PC can restart now" else count(restart.targets.size),
            enabled = restart.targets.isNotEmpty(),
        ) { onAsk(PowerAction.RESTART) },
        OpItem(
            "Shut down $scope", Icons.Rounded.PowerOff,
            supporting = if (shutdown.targets.isEmpty()) "No PC can shut down now" else count(shutdown.targets.size),
            enabled = shutdown.targets.isNotEmpty(), danger = true,
        ) { onAsk(PowerAction.SHUTDOWN) },
    )
}

@Composable
fun PowerConfirmDialog(action: PowerAction, plan: PowerPlan, onConfirm: () -> Unit, onDismiss: () -> Unit) {
    val n = plan.targets.size
    val pcs = if (n == 1) "PC" else "PCs"
    val skipped = buildList {
        if (plan.noPower.isNotEmpty()) add("Skipped, power control is off on the PC: ${plan.noPower.joinToString()}")
        if (plan.offline.isNotEmpty()) add("Offline: ${plan.offline.joinToString()}")
        if (plan.unreachable.isNotEmpty()) add("Can't be woken yet (no MAC address learned): ${plan.unreachable.joinToString()}")
        if (plan.watchOnly.isNotEmpty()) add("Watch only: ${plan.watchOnly.joinToString()}")
    }.joinToString("\n")
    val (heading, body) = when (action) {
        PowerAction.WAKE -> "Turn on $n $pcs?" to "A Wake-on-LAN signal goes to: ${plan.targets.joinToString()}. They take a minute or two to start."
        PowerAction.RESTART -> "Restart $n $pcs?" to "${plan.targets.joinToString()} restart in 5 seconds. Shows running on them stop."
        PowerAction.SHUTDOWN -> "Shut down $n $pcs?" to "${plan.targets.joinToString()} shut down in 5 seconds. Shows running on them stop. Use Turn on to start them again."
    }
    ConfirmDialog(
        title = heading,
        text = if (skipped.isEmpty()) body else "$body\n\n$skipped",
        confirmLabel = when (action) { PowerAction.WAKE -> "Turn on"; PowerAction.RESTART -> "Restart"; PowerAction.SHUTDOWN -> "Shut down" },
        destructive = action != PowerAction.WAKE,
        onConfirm = onConfirm,
        onDismiss = onDismiss,
    )
}
