package com.movingsurface.showcontrol.ui.screens

import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.defaultMinSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.LazyListScope
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.rounded.ArrowBack
import androidx.compose.material.icons.rounded.Add
import androidx.compose.material.icons.rounded.DashboardCustomize
import androidx.compose.material.icons.rounded.Edit
import androidx.compose.material.icons.rounded.PowerSettingsNew
import androidx.compose.material.icons.rounded.Share
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.material3.TopAppBarDefaults
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.movingsurface.showcontrol.data.AgentLiveState
import com.movingsurface.showcontrol.data.AppInfo
import com.movingsurface.showcontrol.data.AppSettings
import com.movingsurface.showcontrol.data.AppState
import com.movingsurface.showcontrol.data.LinkStatus
import com.movingsurface.showcontrol.data.PcEndpoint
import com.movingsurface.showcontrol.data.PowerAction
import com.movingsurface.showcontrol.data.SafetyMode
import com.movingsurface.showcontrol.data.SceneInfo
import com.movingsurface.showcontrol.data.SceneRun
import com.movingsurface.showcontrol.data.ShowRun
import com.movingsurface.showcontrol.data.ShowScene
import com.movingsurface.showcontrol.ui.MainViewModel
import com.movingsurface.showcontrol.ui.components.AddTile
import com.movingsurface.showcontrol.ui.components.HoldToConfirmButton
import com.movingsurface.showcontrol.ui.components.MenuButton
import com.movingsurface.showcontrol.ui.components.OpItem
import com.movingsurface.showcontrol.ui.components.OpSection
import com.movingsurface.showcontrol.ui.components.OperationsSheet
import com.movingsurface.showcontrol.ui.components.PowerConfirmDialog
import com.movingsurface.showcontrol.ui.components.SceneTile
import com.movingsurface.showcontrol.ui.components.SectionHeader
import com.movingsurface.showcontrol.ui.components.StatusDot
import com.movingsurface.showcontrol.ui.components.TileStatus
import com.movingsurface.showcontrol.ui.components.appStateColor
import com.movingsurface.showcontrol.ui.components.appStateLabel
import com.movingsurface.showcontrol.ui.components.formatElapsed
import com.movingsurface.showcontrol.ui.components.gridColumns
import com.movingsurface.showcontrol.ui.components.linkColor
import com.movingsurface.showcontrol.ui.components.powerOps
import com.movingsurface.showcontrol.ui.components.rememberNow
import com.movingsurface.showcontrol.ui.theme.ShowTheme
import java.time.Instant

private data class PcSceneEntry(val pc: PcEndpoint, val scene: SceneInfo, val run: SceneRun?)

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun RoomScreen(
    vm: MainViewModel,
    settings: AppSettings,
    roomId: String,
    onBack: () -> Unit,
    onOpenPc: (String) -> Unit,
    onAddPc: (String) -> Unit,
    onEditTile: (roomId: String, sceneId: String?) -> Unit,
) {
    val room = settings.rooms.firstOrNull { it.id == roomId }
    if (room == null) {
        LaunchedEffect(Unit) { onBack() }
        return
    }
    val live by vm.live.collectAsStateWithLifecycle()
    val runs by vm.showRuns.collectAsStateWithLifecycle()
    val now by rememberNow()
    val pcs = settings.pcsIn(roomId)
    val showScenes = settings.scenesIn(roomId)
    var editingRoom by remember { mutableStateOf(false) }
    var editTiles by rememberSaveable { mutableStateOf(false) }

    val pcScenes = pcs.flatMap { pc ->
        val s = live[pc.id]
        if (s?.online != true) emptyList()
        else s.scenes.map { scene -> PcSceneEntry(pc, scene, s.sceneRun?.takeIf { it.sceneId == scene.id }) }
    }
    val pcSceneGroups = pcScenes.groupBy { it.scene.group?.takeIf { g -> g.isNotBlank() } ?: "PC scenes" }
    val offlineWakeable = pcs.filter { live[it.id]?.online != true && it.mac != null }
    val controllable = pcs.associate { it.id to settings.canControl(it.id, live[it.id]) }
    val roomCanControl = pcs.isEmpty() || controllable.values.any { it }
    var sharing by remember { mutableStateOf(false) }
    var menu by remember { mutableStateOf(false) }
    var confirmPower by remember { mutableStateOf<PowerAction?>(null) }
    val columns = gridColumns(minCellDp = 168, max = 6)

    Scaffold(
        containerColor = MaterialTheme.colorScheme.background,
        topBar = {
            TopAppBar(
                title = {
                    Column {
                        Text(room.name, style = MaterialTheme.typography.titleLarge, maxLines = 1, overflow = TextOverflow.Ellipsis)
                        Text(
                            "${room.kind.label} · ${pcs.size} ${if (pcs.size == 1) "PC" else "PCs"}",
                            style = MaterialTheme.typography.labelMedium,
                            color = MaterialTheme.colorScheme.onSurfaceVariant,
                        )
                    }
                },
                navigationIcon = {
                    IconButton(onClick = onBack) { Icon(Icons.AutoMirrored.Rounded.ArrowBack, contentDescription = "Back") }
                },
                actions = {
                    IconButton(onClick = { onAddPc(roomId) }) { Icon(Icons.Rounded.Add, contentDescription = "Add a PC") }
                    MenuButton(onClick = { menu = true }, description = "Room menu")
                },
                colors = TopAppBarDefaults.topAppBarColors(containerColor = MaterialTheme.colorScheme.background),
            )
        },
        bottomBar = {
            if (pcs.isNotEmpty() && !editTiles && controllable.values.any { it }) {
                HoldToConfirmButton(
                    text = "Hold to stop all",
                    onConfirmed = { vm.stopAllInRoom(roomId) },
                    enabled = pcs.any { live[it.id]?.online == true },
                    modifier = Modifier.navigationBarsPadding().padding(horizontal = 16.dp, vertical = 12.dp),
                )
            }
        },
    ) { padding ->
        LazyColumn(
            contentPadding = PaddingValues(start = 16.dp, end = 16.dp, top = padding.calculateTopPadding(), bottom = padding.calculateBottomPadding() + 24.dp),
        ) {
            if (pcs.isEmpty()) {
                item { EmptyRoom(onAdd = { onAddPc(roomId) }) }
                return@LazyColumn
            }

            item {
                LazyRow(horizontalArrangement = Arrangement.spacedBy(8.dp), contentPadding = PaddingValues(vertical = 8.dp)) {
                    items(pcs, key = { it.id }) { pc -> PcChip(pc, live[pc.id], onClick = { onOpenPc(pc.id) }) }
                }
            }


            // ---- Show scenes: built on the phone, can span PCs ----
            item {
                SectionHeader("Show scenes") {
                    if ((showScenes.isNotEmpty() || editTiles) && roomCanControl) {
                        TextButton(onClick = { editTiles = !editTiles }) {
                            Text(if (editTiles) "Done" else "Edit tiles", color = ShowTheme.colors.accentText)
                        }
                    }
                }
            }
            if (showScenes.isEmpty() && !editTiles && !roomCanControl) {
                item {
                    Text(
                        "No show scenes in this room.",
                        style = MaterialTheme.typography.bodyMedium,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }
            } else if (showScenes.isEmpty() && !editTiles) {
                item {
                    AddTile(
                        text = "New show scene",
                        onClick = { onEditTile(roomId, null) },
                        modifier = Modifier.fillMaxWidth().padding(bottom = 10.dp),
                    )
                }
            } else {
                val cells: List<ShowScene?> = if (editTiles) showScenes + listOf(null) else showScenes
                tileRows(cells, columns) { scene, mod ->
                    if (scene == null) {
                        AddTile("New tile", onClick = { onEditTile(roomId, null) }, modifier = mod)
                    } else {
                        val run = runs[scene.id]
                        val index = showScenes.indexOf(scene)
                        SceneTile(
                            title = scene.name,
                            subtitle = showSubtitle(scene, run, settings),
                            colorKey = scene.color,
                            iconKey = scene.icon,
                            safety = scene.safety,
                            status = run.toStatus(),
                            progress = run?.let { if (it.total > 0) it.step.toFloat() / it.total else null },
                            onFire = { vm.runShowScene(scene) },
                            readOnly = scene.steps.any { st -> st.pcId != null && controllable[st.pcId] == false },
                            editMode = editTiles,
                            onEdit = { onEditTile(roomId, scene.id) },
                            onMoveEarlier = if (index > 0) ({ vm.moveShowScene(scene.id, -1) }) else null,
                            onMoveLater = if (index < showScenes.lastIndex) ({ vm.moveShowScene(scene.id, 1) }) else null,
                            modifier = mod,
                        )
                    }
                }
            }

            // ---- PC scenes and cues: defined in each PC's agent.json ----
            if (pcSceneGroups.isEmpty()) {
                item { SectionHeader("PC scenes") }
                item {
                    Text(
                        if (pcs.none { live[it.id]?.online == true }) "PC scenes and cues appear when the PCs are online."
                        else "No PC scenes yet. Define them in agent.json on each PC.",
                        style = MaterialTheme.typography.bodyMedium,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }
            }
            pcSceneGroups.forEach { (group, entries) ->
                item(key = "group-$group") { SectionHeader(group) }
                tileRows(entries, columns) { entry, mod ->
                    val run = entry.run
                    SceneTile(
                        title = entry.scene.name,
                        subtitle = when {
                            run?.isRunning == true -> "Step ${run.step}/${run.total} · ${run.label}"
                            run?.failed == true -> "Stopped: ${run.message ?: run.label}"
                            run != null -> "Done"
                            pcs.size > 1 -> "${entry.pc.name} · ${entry.scene.steps} ${if (entry.scene.steps == 1) "step" else "steps"}"
                            else -> "${entry.scene.steps} ${if (entry.scene.steps == 1) "step" else "steps"}"
                        },
                        colorKey = entry.scene.color,
                        iconKey = entry.scene.icon,
                        safety = if (entry.scene.confirm) SafetyMode.CONFIRM else SafetyMode.TAP,
                        status = when {
                            run == null -> TileStatus.IDLE
                            run.isRunning -> TileStatus.RUNNING
                            run.failed -> TileStatus.FAILED
                            else -> TileStatus.DONE
                        },
                        progress = run?.let { if (it.total > 0) it.step.toFloat() / it.total else null },
                        onFire = { vm.runScene(entry.pc.id, entry.scene.id) },
                        readOnly = controllable[entry.pc.id] == false,
                        modifier = mod,
                    )
                }
            }

            item { SectionHeader("Applications") }
            pcs.forEach { pc ->
                val s = live[pc.id]
                if (s?.online != true) {
                    item(key = "off-${pc.id}") {
                        OfflineRow(pc, s, onRetry = { vm.reconnect(pc.id) }, onOpen = { onOpenPc(pc.id) })
                    }
                } else {
                    items(s.apps, key = { "${pc.id}/${it.id}" }) { app ->
                        AppRow(
                            app = app,
                            subtitlePrefix = if (pcs.size > 1) pc.name else null,
                            now = now,
                            onStart = { vm.startApp(pc.id, app.id) },
                            onStop = { vm.stopApp(pc.id, app.id) },
                            canControl = controllable[pc.id] != false,
                        )
                    }
                }
            }

        }
    }

    if (menu) {
        val offline = pcs.count { live[it.id]?.online != true }
        OperationsSheet(
            title = room.name,
            subtitle = "${pcs.count { live[it.id]?.online == true }} of ${pcs.size} PCs online",
            sections = listOf(
                OpSection(
                    "PCs in this room",
                    if (pcs.isEmpty()) emptyList()
                    else powerOps("this room's PCs", plan = { vm.powerPlan(it, roomId) }, onAsk = { confirmPower = it }) + OpItem(
                        "Wake offline PCs", Icons.Rounded.PowerSettingsNew,
                        supporting = if (offlineWakeable.isEmpty()) "No offline PC with a known MAC address" else "Wake-on-LAN · ${offlineWakeable.size} of $offline offline",
                        enabled = offlineWakeable.isNotEmpty() || pcs.any { it.viaHub && live[it.id]?.online != true },
                    ) { vm.wakeRoom(roomId) },
                ),
                OpSection(
                    "Room",
                    listOfNotNull(
                        OpItem("Add a PC", Icons.Rounded.Add) { onAddPc(roomId) },
                        if (roomCanControl && pcs.isNotEmpty()) OpItem(if (editTiles) "Done editing tiles" else "Edit tiles", Icons.Rounded.DashboardCustomize) { editTiles = !editTiles } else null,
                        if (showScenes.isNotEmpty() || pcs.isNotEmpty()) OpItem("Share this room's tiles", Icons.Rounded.Share, supporting = "As a code, or publish to your hub") { sharing = true } else null,
                        OpItem("Rename or delete room", Icons.Rounded.Edit) { editingRoom = true },
                    ),
                ),
            ),
            onDismiss = { menu = false },
        )
    }
    confirmPower?.let { action ->
        PowerConfirmDialog(action = action, plan = vm.powerPlan(action, roomId), onConfirm = { vm.runPower(action, roomId) }, onDismiss = { confirmPower = null })
    }
    if (sharing) {
        ShareRoomDialog(
            vm = vm,
            roomName = room.name,
            tiles = showScenes.size,
            pcs = pcs.size,
            hubName = settings.hub?.takeIf { it.role != "viewer" }?.let { it.workspace.ifBlank { "your hub" } },
            roomId = roomId,
            onDismiss = { sharing = false },
        )
    }
    if (editingRoom) {
        RoomEditDialog(
            initial = room,
            onSave = { name, kind -> vm.updateRoom(room.copy(name = name.trim(), kind = kind)) },
            onDelete = { vm.deleteRoom(room.id) },
            onDismiss = { editingRoom = false },
        )
    }
}

/** Lays tiles out [columns] per row inside a LazyColumn (two on a phone, more on a tablet). */
private fun <T> LazyListScope.tileRows(cells: List<T>, columns: Int, content: @Composable (T, Modifier) -> Unit) {
    cells.chunked(columns).forEach { row ->
        item {
            Row(horizontalArrangement = Arrangement.spacedBy(10.dp), modifier = Modifier.fillMaxWidth().padding(bottom = 10.dp)) {
                row.forEach { cell -> content(cell, Modifier.weight(1f)) }
                repeat(columns - row.size) { Spacer(Modifier.weight(1f)) }
            }
        }
    }
}

private fun ShowRun?.toStatus(): TileStatus = when (this?.state) {
    null, ShowRun.State.CANCELLED -> TileStatus.IDLE
    ShowRun.State.RUNNING -> TileStatus.RUNNING
    ShowRun.State.DONE -> TileStatus.DONE
    ShowRun.State.FAILED -> TileStatus.FAILED
}

private fun showSubtitle(scene: ShowScene, run: ShowRun?, settings: AppSettings): String {
    if (run == null) {
        val pcCount = scene.steps.mapNotNull { it.pcId }.distinct().count { id -> settings.pcs.any { it.id == id } }
        val steps = "${scene.steps.size} ${if (scene.steps.size == 1) "step" else "steps"}"
        return if (pcCount > 1) "$steps · $pcCount PCs" else steps
    }
    return when (run.state) {
        ShowRun.State.RUNNING -> "Step ${run.step}/${run.total} · ${run.label}"
        ShowRun.State.FAILED -> "Stopped: ${run.message ?: run.label}"
        ShowRun.State.DONE -> "Done"
        ShowRun.State.CANCELLED -> run.message ?: "Cancelled"
    }
}

@Composable
private fun PcChip(pc: PcEndpoint, state: AgentLiveState?, onClick: () -> Unit) {
    val c = ShowTheme.colors
    val crashed = (state?.crashedCount ?: 0) > 0
    Surface(
        onClick = onClick,
        shape = RoundedCornerShape(14.dp),
        color = if (crashed) c.dangerContainer else c.card,
        border = BorderStroke(1.dp, if (crashed) c.dangerBorder else c.cardBorder),
    ) {
        Column(Modifier.defaultMinSize(minWidth = 96.dp).padding(horizontal = 12.dp, vertical = 10.dp), verticalArrangement = Arrangement.spacedBy(6.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                StatusDot(linkColor(state), 7.dp)
                Text(pc.name, style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.onSurface, maxLines = 1)
            }
            Text(
                when {
                    state == null || state.status == LinkStatus.CONNECTING -> "Connecting"
                    state.status == LinkStatus.UNAUTHORIZED -> "Pair again"
                    state.status == LinkStatus.OFFLINE -> "Offline"
                    crashed -> "${state.crashedCount} crashed"
                    else -> "${state.runningCount}/${state.apps.size} running"
                },
                style = MaterialTheme.typography.bodySmall,
                color = if (crashed) c.danger else MaterialTheme.colorScheme.onSurfaceVariant,
            )
        }
    }
}

@Composable
fun AppRow(
    app: AppInfo,
    subtitlePrefix: String?,
    now: Instant,
    onStart: () -> Unit,
    onStop: () -> Unit,
    trailingExtra: @Composable (() -> Unit)? = null,
    canControl: Boolean = true,
) {
    val c = ShowTheme.colors
    val state = app.appState
    val elapsed = if (state == AppState.RUNNING) formatElapsed(app.since, now) else null
    val subtitle = listOfNotNull(subtitlePrefix, appStateLabel(state) + (elapsed?.let { " $it" } ?: ""), app.message).joinToString(" · ")

    Column {
        Row(Modifier.fillMaxWidth().padding(vertical = 10.dp), verticalAlignment = Alignment.CenterVertically) {
            StatusDot(appStateColor(state))
            Spacer(Modifier.width(12.dp))
            Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(2.dp)) {
                Text(app.name, style = MaterialTheme.typography.bodyLarge, fontWeight = FontWeight.Medium, maxLines = 1, overflow = TextOverflow.Ellipsis)
                Text(
                    subtitle,
                    style = MaterialTheme.typography.labelSmall,
                    color = if (state == AppState.CRASHED) c.danger else MaterialTheme.colorScheme.onSurfaceVariant,
                    maxLines = 2,
                    overflow = TextOverflow.Ellipsis,
                )
            }
            Spacer(Modifier.width(8.dp))
            if (!canControl) {
                if (state == AppState.STARTING || state == AppState.STOPPING) CircularProgressIndicator(strokeWidth = 2.dp, modifier = Modifier.padding(horizontal = 18.dp).size(20.dp))
            } else when (state) {
                AppState.STARTING, AppState.STOPPING ->
                    CircularProgressIndicator(strokeWidth = 2.dp, modifier = Modifier.padding(horizontal = 18.dp).size(20.dp))
                AppState.RUNNING ->
                    OutlinedButton(onClick = onStop, contentPadding = PaddingValues(horizontal = 16.dp)) { Text("Close") }
                AppState.CRASHED ->
                    Button(onClick = onStart, contentPadding = PaddingValues(horizontal = 16.dp)) { Text("Restart") }
                AppState.STOPPED ->
                    Button(onClick = onStart, contentPadding = PaddingValues(horizontal = 16.dp), colors = ButtonDefaults.buttonColors()) { Text("Start") }
            }
            trailingExtra?.invoke()
        }
        HorizontalDivider(color = MaterialTheme.colorScheme.outlineVariant)
    }
}

@Composable
private fun OfflineRow(pc: PcEndpoint, state: AgentLiveState?, onRetry: () -> Unit, onOpen: () -> Unit) {
    val c = ShowTheme.colors
    Column {
        Row(Modifier.fillMaxWidth().padding(vertical = 10.dp), verticalAlignment = Alignment.CenterVertically) {
            StatusDot(linkColor(state))
            Spacer(Modifier.width(12.dp))
            Column(Modifier.weight(1f)) {
                Text(pc.name, style = MaterialTheme.typography.bodyLarge, fontWeight = FontWeight.Medium)
                Text(
                    when (state?.status) {
                        LinkStatus.UNAUTHORIZED -> "Pairing was revoked. Open to pair again."
                        LinkStatus.CONNECTING, null -> "Connecting to ${pc.host}…"
                        else -> "Offline · ${pc.host}"
                    },
                    style = MaterialTheme.typography.labelSmall,
                    color = if (state?.status == LinkStatus.UNAUTHORIZED) c.danger else MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
            when {
                state?.status == LinkStatus.UNAUTHORIZED -> TextButton(onClick = onOpen) { Text("Open", color = c.accentText) }
                else -> TextButton(onClick = onRetry) { Text("Retry", color = c.accentText) }
            }
        }
        HorizontalDivider(color = MaterialTheme.colorScheme.outlineVariant)
    }
}

@Composable
private fun EmptyRoom(onAdd: () -> Unit) {
    val c = ShowTheme.colors
    Surface(
        shape = RoundedCornerShape(18.dp),
        color = c.card,
        border = BorderStroke(1.dp, c.cardBorder),
        modifier = Modifier.fillMaxWidth().padding(top = 16.dp),
    ) {
        Column(Modifier.padding(24.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
            Text("No PCs in this room yet", style = MaterialTheme.typography.titleMedium)
            Text(
                "Run Show Control Agent on each PC, then pair it here with the PIN from its tray menu.",
                style = MaterialTheme.typography.bodyMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
            Button(onClick = onAdd) {
                Icon(Icons.Rounded.Add, contentDescription = null, modifier = Modifier.size(18.dp))
                Spacer(Modifier.width(6.dp))
                Text("Add a PC")
            }
        }
    }
}
