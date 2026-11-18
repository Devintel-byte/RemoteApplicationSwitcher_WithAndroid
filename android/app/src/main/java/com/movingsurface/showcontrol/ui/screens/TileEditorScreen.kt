package com.movingsurface.showcontrol.ui.screens

import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ExperimentalLayoutApi
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.rounded.ArrowBack
import androidx.compose.material.icons.rounded.Add
import androidx.compose.material.icons.rounded.Apps
import androidx.compose.material.icons.rounded.Close
import androidx.compose.material.icons.rounded.Delete
import androidx.compose.material.icons.rounded.KeyboardArrowDown
import androidx.compose.material.icons.rounded.KeyboardArrowUp
import androidx.compose.material.icons.rounded.PlayArrow
import androidx.compose.material.icons.rounded.PowerSettingsNew
import androidx.compose.material.icons.rounded.RestartAlt
import androidx.compose.material.icons.rounded.Stop
import androidx.compose.material.icons.rounded.StopCircle
import androidx.compose.material.icons.rounded.Timer
import androidx.compose.material3.AssistChip
import androidx.compose.material3.Button
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.SegmentedButton
import androidx.compose.material3.SegmentedButtonDefaults
import androidx.compose.material3.SingleChoiceSegmentedButtonRow
import androidx.compose.material3.Surface
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.material3.TopAppBarDefaults
import androidx.compose.material3.rememberModalBottomSheetState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.selected
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.input.KeyboardCapitalization
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.movingsurface.showcontrol.data.AgentLiveState
import com.movingsurface.showcontrol.data.AppSettings
import com.movingsurface.showcontrol.data.PcEndpoint
import com.movingsurface.showcontrol.data.SafetyMode
import com.movingsurface.showcontrol.data.ShowScene
import com.movingsurface.showcontrol.data.ShowSceneRunner
import com.movingsurface.showcontrol.data.ShowStep
import com.movingsurface.showcontrol.data.StepType
import com.movingsurface.showcontrol.ui.MainViewModel
import com.movingsurface.showcontrol.ui.components.ConfirmDialog
import com.movingsurface.showcontrol.ui.components.SceneTile
import com.movingsurface.showcontrol.ui.components.SectionHeader
import com.movingsurface.showcontrol.ui.components.StatusDot
import com.movingsurface.showcontrol.ui.components.TileIconKeys
import com.movingsurface.showcontrol.ui.components.TileStatus
import com.movingsurface.showcontrol.ui.components.linkColor
import com.movingsurface.showcontrol.ui.components.tileIcon
import com.movingsurface.showcontrol.ui.theme.LabelKeys
import com.movingsurface.showcontrol.ui.theme.ShowTheme
import com.movingsurface.showcontrol.ui.theme.labelColors

@OptIn(ExperimentalMaterial3Api::class, ExperimentalLayoutApi::class)
@Composable
fun TileEditorScreen(vm: MainViewModel, settings: AppSettings, roomId: String, sceneId: String?, onBack: () -> Unit) {
    val existing = sceneId?.let { id -> settings.scenes.firstOrNull { it.id == id } }
    var draft by remember(sceneId) { mutableStateOf(existing ?: ShowScene(roomId = roomId, name = "")) }
    var addingStep by remember { mutableStateOf(false) }
    var confirmDelete by remember { mutableStateOf(false) }
    val live by vm.live.collectAsStateWithLifecycle()
    val pcs = settings.pcsIn(roomId)
    val dark = ShowTheme.isDark
    val c = ShowTheme.colors
    val canSave = draft.name.isNotBlank() && draft.steps.isNotEmpty()

    fun updateSteps(transform: (MutableList<ShowStep>) -> Unit) {
        val list = draft.steps.toMutableList()
        transform(list)
        draft = draft.copy(steps = list)
    }

    Scaffold(
        containerColor = MaterialTheme.colorScheme.background,
        topBar = {
            TopAppBar(
                title = { Text(if (existing == null) "New tile" else "Edit tile", style = MaterialTheme.typography.titleLarge) },
                navigationIcon = { IconButton(onClick = onBack) { Icon(Icons.Rounded.Close, contentDescription = "Discard changes") } },
                actions = {
                    TextButton(enabled = canSave, onClick = { vm.saveShowScene(draft.copy(name = draft.name.trim())); onBack() }) {
                        Text("Save", color = if (canSave) c.accentText else MaterialTheme.colorScheme.onSurfaceVariant)
                    }
                },
                colors = TopAppBarDefaults.topAppBarColors(containerColor = MaterialTheme.colorScheme.background),
            )
        },
    ) { padding ->
        Column(
            Modifier
                .fillMaxSize()
                .padding(padding)
                .imePadding()
                .verticalScroll(rememberScrollState())
                .padding(horizontal = 16.dp)
                .padding(bottom = 40.dp),
        ) {
            // Live preview of the tile as it will look on the panel.
            Box(Modifier.fillMaxWidth().padding(vertical = 8.dp), contentAlignment = Alignment.Center) {
                SceneTile(
                    title = draft.name.ifBlank { "Untitled tile" },
                    subtitle = "${draft.steps.size} ${if (draft.steps.size == 1) "step" else "steps"}",
                    colorKey = draft.color,
                    iconKey = draft.icon,
                    safety = draft.safety,
                    status = TileStatus.IDLE,
                    progress = null,
                    onFire = {},
                    modifier = Modifier.width(180.dp),
                )
            }

            OutlinedTextField(
                value = draft.name,
                onValueChange = { draft = draft.copy(name = it.take(32)) },
                label = { Text("Tile name") },
                placeholder = { Text("e.g. Start show") },
                singleLine = true,
                keyboardOptions = KeyboardOptions(capitalization = KeyboardCapitalization.Sentences),
                modifier = Modifier.fillMaxWidth().padding(top = 8.dp),
            )

            SectionHeader("Colour label")
            FlowRow(horizontalArrangement = Arrangement.spacedBy(10.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
                LabelKeys.forEach { key ->
                    val l = labelColors(key, dark)
                    val selected = draft.color == key
                    Box(
                        Modifier
                            .size(44.dp)
                            .clip(CircleShape)
                            .background(l.bg)
                            .border(if (selected) 3.dp else 1.dp, if (selected) l.fg else l.border, CircleShape)
                            .semantics { contentDescription = "$key label"; this.selected = selected }
                            .clickable(role = Role.RadioButton) { draft = draft.copy(color = key) },
                        contentAlignment = Alignment.Center,
                    ) {
                        Box(Modifier.size(18.dp).clip(CircleShape).background(l.fg))
                    }
                }
            }

            SectionHeader("Icon")
            FlowRow(horizontalArrangement = Arrangement.spacedBy(8.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                val l = labelColors(draft.color, dark)
                TileIconKeys.forEach { key ->
                    val selected = draft.icon == key
                    Box(
                        Modifier
                            .size(48.dp)
                            .clip(RoundedCornerShape(12.dp))
                            .background(if (selected) l.bg else c.card)
                            .border(if (selected) 2.dp else 1.dp, if (selected) l.fg else c.cardBorder, RoundedCornerShape(12.dp))
                            .semantics { contentDescription = "$key icon"; this.selected = selected }
                            .clickable(role = Role.RadioButton) { draft = draft.copy(icon = key) },
                        contentAlignment = Alignment.Center,
                    ) {
                        Icon(tileIcon(key), contentDescription = null, tint = if (selected) l.fg else MaterialTheme.colorScheme.onSurfaceVariant)
                    }
                }
            }

            SectionHeader("Safety")
            SingleChoiceSegmentedButtonRow(Modifier.fillMaxWidth()) {
                SafetyMode.entries.forEachIndexed { i, mode ->
                    SegmentedButton(
                        selected = draft.safety == mode,
                        onClick = { draft = draft.copy(safety = mode) },
                        shape = SegmentedButtonDefaults.itemShape(index = i, count = SafetyMode.entries.size),
                        colors = SegmentedButtonDefaults.colors(
                            activeContainerColor = MaterialTheme.colorScheme.secondaryContainer,
                            activeContentColor = MaterialTheme.colorScheme.onSecondaryContainer,
                        ),
                    ) { Text(mode.label) }
                }
            }
            Text(
                when (draft.safety) {
                    SafetyMode.TAP -> "Runs as soon as it's tapped."
                    SafetyMode.CONFIRM -> "Asks \"Run ${draft.name.ifBlank { "this" }}?\" before running."
                    SafetyMode.HOLD -> "Press and hold for a second to run. Best for anything that ends a show."
                },
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                modifier = Modifier.padding(top = 8.dp),
            )

            SectionHeader("Steps") {
                Text("Run in order", style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
            }
            if (draft.steps.isEmpty()) {
                Text(
                    "Add what this tile should do, for example: wake PC2, start Resolume on PC1, start TouchDesigner on PC2, then run the \"Intro\" cue.",
                    style = MaterialTheme.typography.bodyMedium,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                    modifier = Modifier.padding(bottom = 10.dp),
                )
            }
            Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                draft.steps.forEachIndexed { i, step ->
                    StepCard(
                        index = i,
                        step = step,
                        settings = settings,
                        onUp = if (i > 0) ({ updateSteps { it.add(i - 1, it.removeAt(i)) } }) else null,
                        onDown = if (i < draft.steps.lastIndex) ({ updateSteps { it.add(i + 1, it.removeAt(i)) } }) else null,
                        onDelete = { updateSteps { it.removeAt(i) } },
                        onToggleWait = { wait -> updateSteps { it[i] = step.copy(waitUntilReady = wait) } },
                    )
                }
            }
            Spacer(Modifier.height(12.dp))
            OutlinedButton(onClick = { addingStep = true }, enabled = pcs.isNotEmpty(), modifier = Modifier.fillMaxWidth().height(52.dp)) {
                Icon(Icons.Rounded.Add, contentDescription = null, modifier = Modifier.size(18.dp))
                Spacer(Modifier.width(8.dp))
                Text(if (pcs.isEmpty()) "Add a PC to this room first" else "Add step")
            }

            if (existing != null) {
                Spacer(Modifier.height(28.dp))
                TextButton(onClick = { confirmDelete = true }) { Text("Delete tile", color = c.danger) }
            }
        }
    }

    if (addingStep) {
        val sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true)
        ModalBottomSheet(onDismissRequest = { addingStep = false }, sheetState = sheetState) {
            StepPicker(
                pcs = pcs,
                live = live,
                onPicked = { step -> updateSteps { it.add(step) }; addingStep = false },
            )
        }
    }

    if (confirmDelete && existing != null) {
        ConfirmDialog(
            title = "Delete ${existing.name}?",
            text = "The tile is removed from this phone. Nothing changes on the PCs.",
            confirmLabel = "Delete",
            destructive = true,
            onConfirm = { vm.deleteShowScene(existing.id); onBack() },
            onDismiss = { confirmDelete = false },
        )
    }
}

private fun stepIcon(type: StepType): ImageVector = when (type) {
    StepType.START_APP -> Icons.Rounded.PlayArrow
    StepType.STOP_APP -> Icons.Rounded.Stop
    StepType.RESTART_APP -> Icons.Rounded.RestartAlt
    StepType.RUN_PC_SCENE -> Icons.Rounded.Apps
    StepType.STOP_ALL_APPS -> Icons.Rounded.StopCircle
    StepType.WAKE_PC -> Icons.Rounded.PowerSettingsNew
    StepType.WAIT -> Icons.Rounded.Timer
}

private fun waitLabel(type: StepType): String = when (type) {
    StepType.START_APP, StepType.RESTART_APP -> "Wait until it's running"
    StepType.STOP_APP -> "Wait until it's closed"
    StepType.RUN_PC_SCENE -> "Wait until the scene finishes"
    StepType.STOP_ALL_APPS -> "Wait until all are closed"
    StepType.WAKE_PC -> "Wait until the PC is online"
    StepType.WAIT -> ""
}

@Composable
private fun StepCard(
    index: Int,
    step: ShowStep,
    settings: AppSettings,
    onUp: (() -> Unit)?,
    onDown: (() -> Unit)?,
    onDelete: () -> Unit,
    onToggleWait: (Boolean) -> Unit,
) {
    val c = ShowTheme.colors
    val pc = settings.pcs.firstOrNull { it.id == step.pcId }
    Surface(shape = RoundedCornerShape(14.dp), color = c.card, border = BorderStroke(1.dp, c.cardBorder), modifier = Modifier.fillMaxWidth()) {
        Column(Modifier.padding(start = 12.dp, end = 4.dp, top = 8.dp, bottom = 8.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Text("${index + 1}", style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.width(20.dp))
                Icon(stepIcon(step.type), contentDescription = null, tint = c.accentText, modifier = Modifier.size(20.dp))
                Spacer(Modifier.width(10.dp))
                Column(Modifier.weight(1f)) {
                    Text(ShowSceneRunner.describe(step, settings), style = MaterialTheme.typography.bodyLarge, maxLines = 1, overflow = TextOverflow.Ellipsis)
                    if (step.type.needsPc) {
                        Text(
                            pc?.name ?: "PC removed",
                            style = MaterialTheme.typography.labelSmall,
                            color = if (pc == null) c.danger else MaterialTheme.colorScheme.onSurfaceVariant,
                        )
                    }
                }
                IconButton(onClick = { onUp?.invoke() }, enabled = onUp != null) { Icon(Icons.Rounded.KeyboardArrowUp, contentDescription = "Move step up") }
                IconButton(onClick = { onDown?.invoke() }, enabled = onDown != null) { Icon(Icons.Rounded.KeyboardArrowDown, contentDescription = "Move step down") }
                IconButton(onClick = onDelete) { Icon(Icons.Rounded.Delete, contentDescription = "Remove step") }
            }
            if (step.type.canWait) {
                Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.padding(start = 50.dp, end = 8.dp)) {
                    Text(waitLabel(step.type), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.weight(1f))
                    Switch(checked = step.waitUntilReady, onCheckedChange = onToggleWait)
                }
            }
        }
    }
}

/** Bottom-sheet flow: step type → PC → app/scene (or seconds). */
@OptIn(ExperimentalLayoutApi::class)
@Composable
private fun StepPicker(pcs: List<PcEndpoint>, live: Map<String, AgentLiveState>, onPicked: (ShowStep) -> Unit) {
    var type by remember { mutableStateOf<StepType?>(null) }
    var pcId by remember { mutableStateOf<String?>(null) }
    var seconds by remember { mutableStateOf("3") }
    val c = ShowTheme.colors
    val t = type
    val pc = pcs.firstOrNull { it.id == pcId }

    Column(Modifier.fillMaxWidth().navigationBarsPadding().padding(horizontal = 16.dp).padding(bottom = 24.dp)) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            if (t != null) {
                IconButton(onClick = { if (pcId != null && t.needsPc) { pcId = null } else { type = null } }) {
                    Icon(Icons.AutoMirrored.Rounded.ArrowBack, contentDescription = "Back")
                }
            }
            Text(
                when {
                    t == null -> "Add a step"
                    t == StepType.WAIT -> "Wait for how long?"
                    pc == null -> "${t.label}: which PC?"
                    else -> "${t.label} on ${pc.name}"
                },
                style = MaterialTheme.typography.titleMedium,
                modifier = Modifier.padding(vertical = 12.dp),
            )
        }

        when {
            t == null -> StepType.entries.forEach { st ->
                PickRow(icon = stepIcon(st), title = st.label, subtitle = when (st) {
                    StepType.START_APP -> "Launch an app registered on a PC"
                    StepType.STOP_APP -> "Close an app politely, then force it"
                    StepType.RESTART_APP -> "Close and relaunch an app"
                    StepType.RUN_PC_SCENE -> "Run a scene or cue defined on a PC"
                    StepType.STOP_ALL_APPS -> "Close every registered app on a PC"
                    StepType.WAKE_PC -> "Turn a PC on with Wake-on-LAN"
                    StepType.WAIT -> "Pause before the next step"
                }) { type = st }
            }

            t == StepType.WAIT -> {
                FlowRow(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    listOf("1", "3", "5", "10", "30").forEach { v -> AssistChip(onClick = { seconds = v }, label = { Text("$v s") }) }
                }
                OutlinedTextField(
                    value = seconds,
                    onValueChange = { v -> seconds = v.filter { it.isDigit() || it == '.' }.take(6) },
                    label = { Text("Seconds") },
                    singleLine = true,
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Decimal),
                    modifier = Modifier.fillMaxWidth().padding(top = 8.dp),
                )
                val secs = seconds.toDoubleOrNull()
                Button(
                    onClick = { onPicked(ShowStep(type = StepType.WAIT, seconds = secs ?: 0.0, waitUntilReady = false)) },
                    enabled = secs != null && secs > 0 && secs <= 3600,
                    modifier = Modifier.fillMaxWidth().padding(top = 12.dp).height(48.dp),
                ) { Text("Add wait") }
            }

            pc == null -> pcs.forEach { p ->
                val s = live[p.id]
                PickRow(
                    leading = { StatusDot(linkColor(s), 10.dp) },
                    title = p.name,
                    subtitle = when {
                        s?.online == true -> "${s.apps.size} apps · ${s.scenes.size} scenes"
                        t == StepType.WAKE_PC && p.mac == null -> "No MAC address known yet: connect once while it's on"
                        else -> "Offline"
                    },
                ) {
                    if (t == StepType.WAKE_PC || t == StepType.STOP_ALL_APPS) onPicked(ShowStep(type = t, pcId = p.id))
                    else pcId = p.id
                }
            }

            else -> {
                val s = live[pc.id]
                if (s?.online != true) {
                    Text(
                        "${pc.name} is offline, so its apps and scenes can't be listed. Turn it on (or add a \"Wake PC\" step first) and try again.",
                        style = MaterialTheme.typography.bodyMedium,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                        modifier = Modifier.padding(vertical = 8.dp),
                    )
                } else if (t.needsApp) {
                    if (s.apps.isEmpty()) Text("No apps registered on ${pc.name}.", color = MaterialTheme.colorScheme.onSurfaceVariant)
                    s.apps.forEach { app ->
                        PickRow(icon = stepIcon(t), title = app.name, subtitle = app.id) {
                            onPicked(ShowStep(type = t, pcId = pc.id, targetId = app.id, targetName = app.name))
                        }
                    }
                } else if (t.needsScene) {
                    if (s.scenes.isEmpty()) Text("No scenes defined on ${pc.name}.", color = MaterialTheme.colorScheme.onSurfaceVariant)
                    s.scenes.forEach { scene ->
                        PickRow(icon = tileIcon(scene.icon), title = scene.name, subtitle = listOfNotNull(scene.group, "${scene.steps} steps").joinToString(" · ")) {
                            onPicked(ShowStep(type = t, pcId = pc.id, targetId = scene.id, targetName = scene.name))
                        }
                    }
                }
            }
        }
    }
}

@Composable
private fun PickRow(
    title: String,
    subtitle: String,
    icon: ImageVector? = null,
    leading: (@Composable () -> Unit)? = null,
    onClick: () -> Unit,
) {
    Row(
        Modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(12.dp))
            .clickable(onClick = onClick)
            .padding(horizontal = 8.dp, vertical = 12.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Box(Modifier.size(28.dp), contentAlignment = Alignment.Center) {
            when {
                leading != null -> leading()
                icon != null -> Icon(icon, contentDescription = null, tint = ShowTheme.colors.accentText)
            }
        }
        Spacer(Modifier.width(12.dp))
        Column(Modifier.weight(1f)) {
            Text(title, style = MaterialTheme.typography.bodyLarge)
            Text(subtitle, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
        }
    }
}
