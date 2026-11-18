package com.movingsurface.showcontrol.ui.screens

import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ExperimentalLayoutApi
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.rounded.Add
import androidx.compose.material.icons.rounded.Cloud
import androidx.compose.material.icons.rounded.DarkMode
import androidx.compose.material.icons.rounded.Download
import androidx.compose.material.icons.rounded.LightMode
import androidx.compose.material.icons.rounded.MeetingRoom
import androidx.compose.material.icons.rounded.Settings
import androidx.compose.material.icons.rounded.StopCircle
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.DrawerValue
import androidx.compose.material3.ExtendedFloatingActionButton
import androidx.compose.material3.FilterChip
import androidx.compose.material3.FilterChipDefaults
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalDrawerSheet
import androidx.compose.material3.ModalNavigationDrawer
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.rememberDrawerState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.KeyboardCapitalization
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.movingsurface.showcontrol.data.AgentLiveState
import com.movingsurface.showcontrol.data.AppSettings
import com.movingsurface.showcontrol.data.PcEndpoint
import com.movingsurface.showcontrol.data.PowerAction
import com.movingsurface.showcontrol.data.Room
import com.movingsurface.showcontrol.data.RoomKind
import com.movingsurface.showcontrol.ui.MainViewModel
import com.movingsurface.showcontrol.ui.components.BrandMark
import com.movingsurface.showcontrol.ui.components.ConfirmDialog
import com.movingsurface.showcontrol.ui.components.MenuButton
import com.movingsurface.showcontrol.ui.components.OpItem
import com.movingsurface.showcontrol.ui.components.OpSection
import com.movingsurface.showcontrol.ui.components.OperationsList
import com.movingsurface.showcontrol.ui.components.PowerConfirmDialog
import com.movingsurface.showcontrol.ui.components.StatusPill
import com.movingsurface.showcontrol.ui.components.gridColumns
import com.movingsurface.showcontrol.ui.components.linkColor
import com.movingsurface.showcontrol.ui.components.powerOps
import com.movingsurface.showcontrol.ui.theme.ShowTheme
import kotlinx.coroutines.launch

@Composable
fun RoomsScreen(
    vm: MainViewModel,
    settings: AppSettings,
    onOpenRoom: (String) -> Unit,
    onOpenSettings: () -> Unit,
    onImport: () -> Unit = {},
    onOpenHub: () -> Unit = {},
) {
    val live by vm.live.collectAsStateWithLifecycle()
    var filter by rememberSaveable { mutableStateOf<RoomKind?>(null) }
    var showAdd by remember { mutableStateOf(false) }
    var confirmPower by remember { mutableStateOf<PowerAction?>(null) }
    var confirmStopAll by remember { mutableStateOf(false) }
    val dark = ShowTheme.isDark
    val drawer = rememberDrawerState(DrawerValue.Closed)
    val scope = rememberCoroutineScope()

    val rooms = settings.rooms.filter { filter == null || it.kind == filter }
    val kindsInUse = settings.rooms.map { it.kind }.distinct()
    val onlinePcs = settings.pcs.count { live[it.id]?.online == true }
    val columns = gridColumns(minCellDp = 340, max = 3)

    // Everything advanced lives in the side menu (hamburger, top left).
    val sections = listOf(
        OpSection(
            "All PCs",
            if (settings.pcs.isEmpty()) emptyList()
            else powerOps("all PCs", plan = { vm.powerPlan(it) }, onAsk = { confirmPower = it }) + OpItem(
                "Stop all apps", Icons.Rounded.StopCircle,
                supporting = "On every PC, in every room", enabled = onlinePcs > 0, danger = true,
            ) { confirmStopAll = true },
        ),
        OpSection(
            "Rooms",
            listOf(
                OpItem("Add a room", Icons.Rounded.Add) { showAdd = true },
                OpItem("Import a shared room", Icons.Rounded.Download, supporting = "From a share code or your hub") { onImport() },
            ),
        ),
        OpSection(
            "App",
            listOf(
                OpItem("Remote access", Icons.Rounded.Cloud, supporting = settings.hub?.let { it.workspace.ifBlank { it.url } } ?: "Sign in to a hub") { onOpenHub() },
                OpItem(if (dark) "Light mode" else "Dark mode", if (dark) Icons.Rounded.LightMode else Icons.Rounded.DarkMode) { vm.toggleTheme(dark) },
                OpItem("Settings", Icons.Rounded.Settings) { onOpenSettings() },
            ),
        ),
    )

    ModalNavigationDrawer(
        drawerState = drawer,
        drawerContent = {
            ModalDrawerSheet(drawerContainerColor = MaterialTheme.colorScheme.surfaceContainer) {
                Column(Modifier.verticalScroll(rememberScrollState()).padding(horizontal = 12.dp, vertical = 16.dp)) {
                    Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.padding(horizontal = 12.dp, vertical = 8.dp)) {
                        BrandMark(brandName = settings.brandName, logoVersion = settings.logoVersion, size = 40.dp)
                        Spacer(Modifier.width(12.dp))
                        Column {
                            Text(settings.brandName.ifBlank { "Show Control" }, style = MaterialTheme.typography.titleMedium, maxLines = 1, overflow = TextOverflow.Ellipsis)
                            Text(
                                if (settings.pcs.isEmpty()) "No PCs yet" else "$onlinePcs of ${settings.pcs.size} PCs online",
                                style = MaterialTheme.typography.bodySmall,
                                color = MaterialTheme.colorScheme.onSurfaceVariant,
                            )
                        }
                    }
                    OperationsList(sections, onPicked = { scope.launch { drawer.close() } }, modifier = Modifier.padding(top = 8.dp))
                }
            }
        },
    ) {
        Scaffold(
            containerColor = MaterialTheme.colorScheme.background,
            floatingActionButton = {
                ExtendedFloatingActionButton(
                    onClick = { showAdd = true },
                    icon = { Icon(Icons.Rounded.Add, contentDescription = null) },
                    text = { Text("Add room") },
                    containerColor = MaterialTheme.colorScheme.primary,
                    contentColor = MaterialTheme.colorScheme.onPrimary,
                )
            },
        ) { padding ->
            LazyColumn(
                contentPadding = PaddingValues(start = 20.dp, end = 20.dp, top = padding.calculateTopPadding() + 8.dp, bottom = 120.dp),
                verticalArrangement = Arrangement.spacedBy(12.dp),
            ) {
                item {
                    BrandHeader(
                        brandName = settings.brandName,
                        logoVersion = settings.logoVersion,
                        onOpenMenu = { scope.launch { drawer.open() } },
                    )
                }

                if (kindsInUse.size > 1) {
                    item {
                        LazyRow(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                            item { KindChip("All rooms", filter == null) { filter = null } }
                            items(kindsInUse) { kind -> KindChip(kind.label, filter == kind) { filter = kind } }
                        }
                    }
                }

                item {
                    Row(Modifier.fillMaxWidth().padding(top = 8.dp), verticalAlignment = Alignment.Bottom) {
                        Text("Rooms", style = MaterialTheme.typography.headlineMedium, modifier = Modifier.weight(1f))
                        if (settings.pcs.isNotEmpty()) {
                            Text(
                                "$onlinePcs of ${settings.pcs.size} PCs online",
                                style = MaterialTheme.typography.labelMedium,
                                color = MaterialTheme.colorScheme.onSurfaceVariant,
                            )
                        }
                    }
                }

                if (settings.rooms.isEmpty()) {
                    item { EmptyRooms(onAdd = { showAdd = true }) }
                }

                // Room cards: one column on phones, two or three on tablets.
                items(rooms.chunked(columns), key = { row -> row.first().id }) { row ->
                    Row(horizontalArrangement = Arrangement.spacedBy(12.dp), modifier = Modifier.fillMaxWidth()) {
                        row.forEach { room ->
                            RoomCard(room = room, pcs = settings.pcsIn(room.id), live = live, onClick = { onOpenRoom(room.id) }, modifier = Modifier.weight(1f))
                        }
                        repeat(columns - row.size) { Spacer(Modifier.weight(1f)) }
                    }
                }
            }
        }
    }

    confirmPower?.let { action ->
        PowerConfirmDialog(action = action, plan = vm.powerPlan(action), onConfirm = { vm.runPower(action) }, onDismiss = { confirmPower = null })
    }
    if (confirmStopAll) {
        ConfirmDialog(
            title = "Stop all apps on every PC?",
            text = "Every app and running show scene stops, in every room. PCs stay on.",
            confirmLabel = "Stop all",
            destructive = true,
            onConfirm = { vm.stopAllEverywhere() },
            onDismiss = { confirmStopAll = false },
        )
    }

    if (showAdd) {
        RoomEditDialog(
            initial = null,
            onSave = { name, kind -> vm.addRoom(name, kind) },
            onDelete = null,
            onDismiss = { showAdd = false },
        )
    }
}

/** The hamburger (all advanced operations), then the brand. */
@Composable
private fun BrandHeader(brandName: String, logoVersion: Long, onOpenMenu: () -> Unit) {
    Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.fillMaxWidth().padding(top = 8.dp)) {
        MenuButton(onClick = onOpenMenu, description = "Open menu")
        Spacer(Modifier.width(4.dp))
        BrandMark(brandName = brandName, logoVersion = logoVersion)
        Spacer(Modifier.width(12.dp))
        Column(Modifier.weight(1f)) {
            Text(brandName.ifBlank { "Show Control" }, style = MaterialTheme.typography.titleMedium, maxLines = 1, overflow = TextOverflow.Ellipsis)
            Text("Show control", style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
        }
    }
}

@Composable
private fun KindChip(label: String, selected: Boolean, onClick: () -> Unit) {
    FilterChip(
        selected = selected,
        onClick = onClick,
        label = { Text(label) },
        colors = FilterChipDefaults.filterChipColors(
            selectedContainerColor = MaterialTheme.colorScheme.onBackground,
            selectedLabelColor = MaterialTheme.colorScheme.background,
        ),
    )
}

@Composable
private fun RoomCard(room: Room, pcs: List<PcEndpoint>, live: Map<String, AgentLiveState>, onClick: () -> Unit, modifier: Modifier = Modifier) {
    val c = ShowTheme.colors
    val states = pcs.map { live[it.id] }
    val online = states.count { it?.online == true }
    val crashed = states.sumOf { it?.crashedCount ?: 0 }
    val running = states.sumOf { it?.runningCount ?: 0 }
    val totalApps = states.sumOf { it?.apps?.size ?: 0 }

    Surface(
        onClick = onClick,
        shape = RoundedCornerShape(18.dp),
        color = c.card,
        border = BorderStroke(1.dp, c.cardBorder),
        modifier = modifier.fillMaxWidth(),
    ) {
        Column(Modifier.padding(18.dp), verticalArrangement = Arrangement.spacedBy(14.dp)) {
            Row(verticalAlignment = Alignment.Top) {
                Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(4.dp)) {
                    Text(room.name, style = MaterialTheme.typography.titleLarge)
                    Text(
                        "${room.kind.label} · ${pcs.size} ${if (pcs.size == 1) "PC" else "PCs"}",
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }
                when {
                    pcs.isEmpty() -> StatusPill("Empty", c.idle, MaterialTheme.colorScheme.surfaceVariant)
                    crashed > 0 -> StatusPill("$crashed crashed", c.danger, c.dangerContainer)
                    online == pcs.size -> StatusPill("Live", c.ok, c.okContainer)
                    online > 0 -> StatusPill("$online/${pcs.size} online", c.warn, c.warnContainer)
                    else -> StatusPill("Offline", c.idle, MaterialTheme.colorScheme.surfaceVariant)
                }
            }

            Row(
                Modifier.fillMaxWidth().clip(RoundedCornerShape(12.dp)).background(c.inset).padding(horizontal = 14.dp, vertical = 12.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(2.dp)) {
                    if (pcs.isEmpty()) {
                        Text("Tap to add a PC", style = MaterialTheme.typography.bodyMedium)
                    } else {
                        Text("Apps running", style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                        Text(if (online == 0) "–" else "$running of $totalApps", style = MaterialTheme.typography.bodyMedium, fontWeight = FontWeight.Medium)
                    }
                }
                Row(horizontalArrangement = Arrangement.spacedBy(5.dp)) {
                    states.take(8).forEach { s ->
                        Box(Modifier.width(22.dp).height(6.dp).clip(RoundedCornerShape(3.dp)).background(linkColor(s)))
                    }
                }
            }
        }
    }
}

@Composable
private fun EmptyRooms(onAdd: () -> Unit) {
    val c = ShowTheme.colors
    Surface(shape = RoundedCornerShape(18.dp), color = c.card, border = BorderStroke(1.dp, c.cardBorder), modifier = Modifier.fillMaxWidth()) {
        Column(Modifier.padding(24.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
            Icon(Icons.Rounded.MeetingRoom, contentDescription = null, tint = c.accentText, modifier = Modifier.size(28.dp))
            Text("Set up your first room", style = MaterialTheme.typography.titleMedium)
            Text(
                "A room groups the PCs for one event, installation or space. Add a room, then pair the PCs that run its content.",
                style = MaterialTheme.typography.bodyMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
            TextButton(onClick = onAdd, contentPadding = PaddingValues(0.dp)) {
                Text("Add a room", color = c.accentText)
            }
        }
    }
}

@OptIn(ExperimentalLayoutApi::class)
@Composable
fun RoomEditDialog(
    initial: Room?,
    onSave: (String, RoomKind) -> Unit,
    onDelete: (() -> Unit)?,
    onDismiss: () -> Unit,
) {
    var name by rememberSaveable { mutableStateOf(initial?.name ?: "") }
    var kind by rememberSaveable { mutableStateOf(initial?.kind ?: RoomKind.PRODUCTION) }
    var confirmDelete by remember { mutableStateOf(false) }

    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text(if (initial == null) "New room" else "Edit room") },
        text = {
            Column(verticalArrangement = Arrangement.spacedBy(14.dp)) {
                OutlinedTextField(
                    value = name,
                    onValueChange = { name = it.take(40) },
                    label = { Text("Name") },
                    placeholder = { Text("e.g. Event Hall") },
                    singleLine = true,
                    keyboardOptions = KeyboardOptions(capitalization = KeyboardCapitalization.Words),
                    modifier = Modifier.fillMaxWidth(),
                )
                Text("Type", style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
                FlowRow(horizontalArrangement = Arrangement.spacedBy(8.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                    RoomKind.entries.forEach { k -> KindChip(k.label, kind == k) { kind = k } }
                }
                if (onDelete != null) {
                    TextButton(onClick = { confirmDelete = true }, contentPadding = PaddingValues(0.dp)) {
                        Text("Delete room", color = ShowTheme.colors.danger)
                    }
                }
            }
        },
        confirmButton = {
            TextButton(enabled = name.isNotBlank(), onClick = { onSave(name, kind); onDismiss() }) {
                Text("Save", color = if (name.isNotBlank()) ShowTheme.colors.accentText else MaterialTheme.colorScheme.onSurfaceVariant)
            }
        },
        dismissButton = { TextButton(onClick = onDismiss) { Text("Cancel") } },
    )

    if (confirmDelete && onDelete != null) {
        ConfirmDialog(
            title = "Delete ${initial?.name}?",
            text = "The room and its paired PCs are removed from this phone. Nothing changes on the PCs.",
            confirmLabel = "Delete",
            destructive = true,
            onConfirm = { onDelete(); onDismiss() },
            onDismiss = { confirmDelete = false },
        )
    }
}
