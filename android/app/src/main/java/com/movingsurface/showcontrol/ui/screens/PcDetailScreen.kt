package com.movingsurface.showcontrol.ui.screens

import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.rounded.ArrowBack
import androidx.compose.material.icons.rounded.Delete
import androidx.compose.material.icons.rounded.Edit
import androidx.compose.material.icons.rounded.MoreVert
import androidx.compose.material.icons.rounded.PowerOff
import androidx.compose.material.icons.rounded.PowerSettingsNew
import androidx.compose.material.icons.rounded.Refresh
import androidx.compose.material.icons.rounded.RestartAlt
import androidx.compose.material.icons.rounded.StopCircle
import androidx.compose.material.icons.rounded.Visibility
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.FilterChip
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.material3.TopAppBarDefaults
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateListOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.window.Dialog
import androidx.compose.ui.window.DialogProperties
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.compose.LocalLifecycleOwner
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.repeatOnLifecycle
import com.movingsurface.showcontrol.data.AgentLiveState
import com.movingsurface.showcontrol.data.AppSettings
import com.movingsurface.showcontrol.data.AppState
import com.movingsurface.showcontrol.data.DisplayInfo
import com.movingsurface.showcontrol.data.LinkStatus
import com.movingsurface.showcontrol.data.LiveViewQuality
import com.movingsurface.showcontrol.data.PcEndpoint
import com.movingsurface.showcontrol.data.ScreenshotFrame
import com.movingsurface.showcontrol.ui.MainViewModel
import com.movingsurface.showcontrol.ui.components.ConfirmDialog
import com.movingsurface.showcontrol.ui.components.LinkPill
import com.movingsurface.showcontrol.ui.components.MenuButton
import com.movingsurface.showcontrol.ui.components.OpItem
import com.movingsurface.showcontrol.ui.components.OpSection
import com.movingsurface.showcontrol.ui.components.OperationsSheet
import com.movingsurface.showcontrol.ui.components.SectionHeader
import com.movingsurface.showcontrol.ui.components.StatusDot
import com.movingsurface.showcontrol.ui.components.formatUptime
import com.movingsurface.showcontrol.ui.components.rememberNow
import com.movingsurface.showcontrol.ui.theme.ShowTheme
import com.movingsurface.showcontrol.ui.theme.labelColors
import kotlinx.coroutines.awaitCancellation
import kotlinx.coroutines.delay

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun PcDetailScreen(vm: MainViewModel, settings: AppSettings, pcId: String, onBack: () -> Unit) {
    val pc = settings.pcs.firstOrNull { it.id == pcId }
    if (pc == null) {
        LaunchedEffect(Unit) { onBack() }
        return
    }
    val live by vm.live.collectAsStateWithLifecycle()
    val state = live[pcId]
    val now by rememberNow()
    var menu by remember { mutableStateOf(false) }
    var renaming by remember { mutableStateOf(false) }
    var confirmRemove by remember { mutableStateOf(false) }
    var confirmPower by remember { mutableStateOf<String?>(null) }
    var confirmStopAll by remember { mutableStateOf(false) }
    val canControl = settings.canControl(pc.id, state)

    Scaffold(
        containerColor = MaterialTheme.colorScheme.background,
        topBar = {
            TopAppBar(
                title = {
                    Column {
                        Text(pc.name, style = MaterialTheme.typography.titleLarge, maxLines = 1, overflow = TextOverflow.Ellipsis)
                        Text(
                            "${pc.host}:${pc.port}" + (state?.agent?.version?.takeIf { it.isNotBlank() }?.let { " · agent v$it" } ?: ""),
                            style = MaterialTheme.typography.labelMedium,
                            color = MaterialTheme.colorScheme.onSurfaceVariant,
                        )
                    }
                },
                navigationIcon = { IconButton(onClick = onBack) { Icon(Icons.AutoMirrored.Rounded.ArrowBack, contentDescription = "Back") } },
                actions = {
                    LinkPill(state)
                    MenuButton(onClick = { menu = true }, description = "PC menu")
                },
                colors = TopAppBarDefaults.topAppBarColors(containerColor = MaterialTheme.colorScheme.background),
            )
        },
    ) { padding ->
        LazyColumn(contentPadding = PaddingValues(start = 16.dp, end = 16.dp, top = padding.calculateTopPadding(), bottom = 40.dp)) {
            when (state?.status) {
                LinkStatus.UNAUTHORIZED -> item { RepairCard(vm, pc) }
                LinkStatus.OFFLINE -> item {
                    OfflineCard(pc, state!!, canWake = pc.mac != null || pc.viaHub, onRetry = { vm.reconnect(pc.id) })
                }
                LinkStatus.CONNECTING, null -> item {
                    Row(Modifier.padding(vertical = 24.dp), verticalAlignment = Alignment.CenterVertically) {
                        CircularProgressIndicator(strokeWidth = 2.dp, modifier = Modifier.width(20.dp).height(20.dp))
                        Spacer(Modifier.width(12.dp))
                        Text("Connecting to ${pc.host}…", color = MaterialTheme.colorScheme.onSurfaceVariant)
                    }
                }
                LinkStatus.ONLINE -> {
                    val st = state!!
                    if (!canControl) item { WatchOnlyBanner(pc.viaHub) }
                    if (st.agent?.canScreenshot == true) {
                        item { LiveOutputCard(vm, pc.id, st.agent.displays, st.agent.canStream, settings.liveView) }
                    }
                    item { StatsGrid(st) }

                    if (st.scenes.isNotEmpty()) {
                        item { SectionHeader("Scenes on this PC") }
                        items(st.scenes, key = { "scene-${it.id}" }) { scene ->
                            val run = st.sceneRun?.takeIf { it.sceneId == scene.id }
                            val label = labelColors(scene.color, ShowTheme.isDark)
                            Column {
                                Row(Modifier.fillMaxWidth().padding(vertical = 10.dp), verticalAlignment = Alignment.CenterVertically) {
                                    StatusDot(label.fg)
                                    Spacer(Modifier.width(12.dp))
                                    Column(Modifier.weight(1f)) {
                                        Text(scene.name, style = MaterialTheme.typography.bodyLarge)
                                        Text(
                                            when {
                                                run?.isRunning == true -> "Step ${run.step}/${run.total} · ${run.label}"
                                                run?.failed == true -> "Stopped: ${run.message ?: run.label}"
                                                run != null -> "Done"
                                                else -> "${scene.steps} steps"
                                            },
                                            style = MaterialTheme.typography.labelSmall,
                                            color = if (run?.failed == true) ShowTheme.colors.danger else MaterialTheme.colorScheme.onSurfaceVariant,
                                        )
                                    }
                                    if (run?.isRunning == true) CircularProgressIndicator(strokeWidth = 2.dp, modifier = Modifier.padding(horizontal = 18.dp).width(20.dp).height(20.dp))
                                    else if (canControl) OutlinedButton(onClick = { vm.runScene(pc.id, scene.id) }) { Text("Run") }
                                }
                                HorizontalDivider(color = MaterialTheme.colorScheme.outlineVariant)
                            }
                        }
                    }

                    item { SectionHeader("Applications") }
                    if (st.apps.isEmpty()) {
                        item {
                            Text(
                                "No apps registered on this PC. Add them in agent.json (tray menu → Edit apps & scenes).",
                                style = MaterialTheme.typography.bodyMedium,
                                color = MaterialTheme.colorScheme.onSurfaceVariant,
                            )
                        }
                    }
                    items(st.apps, key = { "app-${it.id}" }) { app ->
                        var appMenu by remember { mutableStateOf(false) }
                        AppRow(
                            app = app,
                            subtitlePrefix = null,
                            now = now,
                            onStart = { vm.startApp(pc.id, app.id) },
                            onStop = { vm.stopApp(pc.id, app.id) },
                            canControl = canControl,
                            trailingExtra = if (!canControl) null else ({
                                Box {
                                    IconButton(onClick = { appMenu = true }) { Icon(Icons.Rounded.MoreVert, contentDescription = "More for ${app.name}") }
                                    DropdownMenu(expanded = appMenu, onDismissRequest = { appMenu = false }) {
                                        DropdownMenuItem(text = { Text("Restart") }, onClick = { appMenu = false; vm.restartApp(pc.id, app.id) })
                                        DropdownMenuItem(
                                            text = { Text("Force close", color = ShowTheme.colors.danger) },
                                            enabled = app.appState != AppState.STOPPED,
                                            onClick = { appMenu = false; vm.stopApp(pc.id, app.id, force = true) },
                                        )
                                    }
                                }
                            }),
                        )
                    }

                }
            }
        }
    }

    if (renaming) {
        var name by remember { mutableStateOf(pc.name) }
        AlertDialog(
            onDismissRequest = { renaming = false },
            title = { Text("Rename PC") },
            text = { OutlinedTextField(value = name, onValueChange = { name = it.take(40) }, singleLine = true, label = { Text("Name") }) },
            confirmButton = {
                TextButton(enabled = name.isNotBlank(), onClick = { vm.renamePc(pc.id, name); renaming = false }) {
                    Text("Save", color = ShowTheme.colors.accentText)
                }
            },
            dismissButton = { TextButton(onClick = { renaming = false }) { Text("Cancel") } },
        )
    }
    if (confirmRemove) {
        ConfirmDialog(
            title = "Remove ${pc.name}?",
            text = "This phone forgets the PC. The agent keeps running; delete this phone from its agent.json to revoke access fully.",
            confirmLabel = "Remove",
            destructive = true,
            onConfirm = { vm.deletePc(pc.id) },
            onDismiss = { confirmRemove = false },
        )
    }
    if (menu) {
        val online = state?.online == true
        val allowPower = state?.agent?.allowPower == true
        OperationsSheet(
            title = pc.name,
            subtitle = if (online) "Online · ${pc.host}" else "Offline · ${pc.host}",
            sections = listOf(
                OpSection(
                    "Power",
                    listOf(
                        OpItem(
                            "Turn on (Wake-on-LAN)", Icons.Rounded.PowerSettingsNew,
                            supporting = when { online -> "Already on"; pc.mac != null || pc.viaHub -> "Sends a magic packet"; else -> "No MAC address learned yet" },
                            enabled = !online && (pc.mac != null || pc.viaHub),
                        ) { vm.wakePc(pc.id) },
                        OpItem(
                            "Restart PC", Icons.Rounded.RestartAlt,
                            supporting = if (online && !allowPower) "Turned off on the PC (dashboard → Settings)" else null,
                            enabled = online && allowPower && canControl,
                        ) { confirmPower = "restart" },
                        OpItem(
                            "Shut down PC", Icons.Rounded.PowerOff,
                            supporting = if (online && !allowPower) "Turned off on the PC (dashboard → Settings)" else null,
                            enabled = online && allowPower && canControl, danger = true,
                        ) { confirmPower = "shutdown" },
                    ),
                ),
                OpSection(
                    "Apps",
                    listOfNotNull(
                        if (canControl) OpItem("Stop all apps", Icons.Rounded.StopCircle, enabled = online, danger = true) { confirmStopAll = true } else null,
                        OpItem("Reconnect", Icons.Rounded.Refresh) { vm.reconnect(pc.id) },
                    ),
                ),
                OpSection(
                    "This PC",
                    listOf(
                        OpItem("Rename", Icons.Rounded.Edit) { renaming = true },
                        OpItem("Remove from this phone", Icons.Rounded.Delete, danger = true) { confirmRemove = true },
                    ),
                ),
            ),
            onDismiss = { menu = false },
        )
    }
    if (confirmStopAll) {
        ConfirmDialog(
            title = "Stop all apps on ${pc.name}?",
            text = "Every registered app on this PC is closed.",
            confirmLabel = "Stop all",
            destructive = true,
            onConfirm = { vm.stopAllOnPc(pc.id) },
            onDismiss = { confirmStopAll = false },
        )
    }
    confirmPower?.let { action ->
        ConfirmDialog(
            title = if (action == "restart") "Restart ${pc.name}?" else "Shut down ${pc.name}?",
            text = if (action == "restart") "The PC restarts in 5 seconds. Running shows on it will stop."
            else "The PC shuts down in 5 seconds. You'll need Wake-on-LAN or someone on site to turn it back on.",
            confirmLabel = if (action == "restart") "Restart" else "Shut down",
            destructive = true,
            onConfirm = { vm.power(pc.id, action) },
            onDismiss = { confirmPower = null },
        )
    }
}

@Composable
private fun StatsGrid(state: AgentLiveState) {
    val s = state.stats
    val ramPct = if (s != null && s.ramTotalMb > 0) s.ramUsedMb.toFloat() / s.ramTotalMb else 0f
    Column(verticalArrangement = Arrangement.spacedBy(10.dp), modifier = Modifier.padding(top = 8.dp)) {
        Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
            StatCard("CPU", s?.let { "${it.cpu.toInt()}%" } ?: "–", bar = (s?.cpu?.toFloat() ?: 0f) / 100f, modifier = Modifier.weight(1f))
            StatCard(
                "RAM",
                s?.let { "%.1f / %.0f GB".format(it.ramUsedMb / 1024f, it.ramTotalMb / 1024f) } ?: "–",
                bar = ramPct,
                modifier = Modifier.weight(1f),
            )
        }
        Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
            StatCard("Uptime", s?.let { formatUptime(it.uptimeSec) } ?: "–", bar = null, modifier = Modifier.weight(1f))
            StatCard("Apps running", "${state.runningCount} / ${state.apps.size}", bar = null, modifier = Modifier.weight(1f))
        }
    }
}

@Composable
private fun StatCard(label: String, value: String, bar: Float?, modifier: Modifier) {
    val c = ShowTheme.colors
    Surface(shape = RoundedCornerShape(14.dp), color = c.card, border = BorderStroke(1.dp, c.cardBorder), modifier = modifier) {
        Column(Modifier.padding(14.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
            Text(label, style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
            Text(value, style = MaterialTheme.typography.titleLarge)
            if (bar != null) {
                val v = bar.coerceIn(0f, 1f)
                val color: Color = when {
                    v > 0.9f -> c.danger
                    v > 0.7f -> c.warn
                    else -> c.ok
                }
                Box(Modifier.fillMaxWidth().height(4.dp).clip(RoundedCornerShape(2.dp)).background(MaterialTheme.colorScheme.outlineVariant)) {
                    Box(Modifier.fillMaxWidth(v).fillMaxHeight().clip(RoundedCornerShape(2.dp)).background(color))
                }
            }
        }
    }
}

@Composable
private fun OfflineCard(pc: PcEndpoint, state: AgentLiveState, canWake: Boolean, onRetry: () -> Unit) {
    val c = ShowTheme.colors
    Surface(shape = RoundedCornerShape(16.dp), color = c.card, border = BorderStroke(1.dp, c.cardBorder), modifier = Modifier.fillMaxWidth().padding(top = 12.dp)) {
        Column(Modifier.padding(18.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
            Text("Can't reach ${pc.name}", style = MaterialTheme.typography.titleMedium)
            state.error?.let { Text(it, style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant) }
            Text(
                "Check that the PC is on, Show Control Agent is in its tray, and this phone is on the same network. Retrying automatically.",
                style = MaterialTheme.typography.bodyMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
            OutlinedButton(onClick = onRetry) { Text("Retry now") }
            Text(
                if (canWake) "To turn it on, open the menu (☰) and choose Turn on (Wake-on-LAN)."
                else "Wake-on-LAN becomes available after this phone has connected to the PC once.",
                style = MaterialTheme.typography.labelSmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
        }
    }
}

@Composable
private fun RepairCard(vm: MainViewModel, pc: PcEndpoint) {
    val c = ShowTheme.colors
    val pair by vm.pair.collectAsStateWithLifecycle()
    var pin by remember { mutableStateOf("") }
    Surface(shape = RoundedCornerShape(16.dp), color = c.dangerContainer, border = BorderStroke(1.dp, c.dangerBorder), modifier = Modifier.fillMaxWidth().padding(top = 12.dp)) {
        Column(Modifier.padding(18.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
            val firstTime = pc.token.isBlank()
            Text(if (firstTime) "Pair this PC" else "Pair this PC again", style = MaterialTheme.typography.titleMedium)
            Text(
                if (firstTime) "This PC came with a shared room. Enter the PIN from its agent (tray menu or dashboard → Phones) to connect."
                else "The PC no longer accepts this phone. Enter the PIN from its tray menu to reconnect.",
                style = MaterialTheme.typography.bodyMedium,
            )
            OutlinedTextField(
                value = pin,
                onValueChange = { v -> pin = v.filter { it.isDigit() }.take(6) },
                label = { Text("Pairing PIN") },
                singleLine = true,
                keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.NumberPassword),
            )
            pair.error?.let { Text(it, color = c.danger, style = MaterialTheme.typography.bodySmall) }
            Button(enabled = pin.length == 6 && !pair.busy, onClick = { vm.repairPc(pc, pin) { pin = "" } }) {
                Text(if (pair.busy) "Pairing…" else "Pair again")
            }
        }
    }
}

/**
 * What the PC is showing, live. Agents 1.3+ push a stream of frames (up to 30 fps, see Settings → Live
 * view) for as long as this screen is open and the app is in front; older agents are polled for a
 * snapshot every 2 seconds instead.
 */
@Composable
private fun LiveOutputCard(vm: MainViewModel, pcId: String, displays: List<DisplayInfo>, canStream: Boolean, quality: LiveViewQuality) {
    val c = ShowTheme.colors
    val frames by vm.screenshots.collectAsStateWithLifecycle()
    val frame = frames[pcId]
    var display by rememberSaveable(pcId) { mutableStateOf(displays.firstOrNull { it.primary }?.index ?: 0) }
    var fullscreen by remember { mutableStateOf(false) }
    val now by rememberNow()
    val lifecycle = LocalLifecycleOwner.current.lifecycle

    LaunchedEffect(pcId, display, fullscreen, canStream, quality) {
        lifecycle.repeatOnLifecycle(Lifecycle.State.RESUMED) {
            if (canStream) {
                vm.startLiveView(pcId, display, fullscreen)
                try { awaitCancellation() } finally { vm.stopLiveView(pcId) }
            } else {
                while (true) {
                    vm.requestScreenshot(pcId, display, if (fullscreen) 1280 else 720)
                    delay(2_000)
                }
            }
        }
    }

    // Frames per second actually shown, from the last second of frame arrivals.
    val arrivals = remember { mutableStateListOf<Long>() }
    LaunchedEffect(frame?.seq, frame?.at) {
        if (frame?.live == true && frame.bitmap != null) {
            val t = System.currentTimeMillis()
            arrivals.add(t)
            arrivals.removeAll { t - it > 1_000 }
        }
    }
    val fps = arrivals.count { now.toEpochMilli() - it <= 1_500 }

    val badge: @Composable () -> Unit = {
        val f = frame
        if (f != null && f.bitmap != null) {
            val err = f.error
            when {
                err != null -> OverlayLabel("paused · $err", warn = true)
                f.live -> OverlayLabel(if (fps > 0) "LIVE · $fps fps" else "LIVE", live = true)
                else -> OverlayLabel("${((now.toEpochMilli() - f.at) / 1000).coerceAtLeast(0)}s ago")
            }
        }
    }

    Column(Modifier.padding(top = 8.dp)) {
        Surface(
            onClick = { if (frame?.bitmap != null) fullscreen = true },
            shape = RoundedCornerShape(16.dp),
            color = Color(0xFF05070A),
            border = BorderStroke(1.dp, c.cardBorder),
            modifier = Modifier.fillMaxWidth(),
        ) {
            Box(Modifier.fillMaxWidth().aspectRatio(frame?.aspect() ?: (16f / 9f)), contentAlignment = Alignment.Center) {
                val bmp = frame?.bitmap
                if (bmp != null) {
                    Image(
                        bitmap = bmp.asImageBitmap(),
                        contentDescription = "Live output of display ${display + 1}",
                        contentScale = ContentScale.Fit,
                        modifier = Modifier.fillMaxSize(),
                    )
                } else if (frame?.error != null) {
                    Text(frame.error, style = MaterialTheme.typography.bodySmall, color = Color(0xFFB8C0CC), modifier = Modifier.padding(16.dp))
                } else {
                    CircularProgressIndicator(strokeWidth = 2.dp, color = Color.White, modifier = Modifier.size(24.dp))
                }
                Row(Modifier.align(Alignment.TopStart).fillMaxWidth().padding(10.dp)) {
                    OverlayLabel(displays.firstOrNull { it.index == display }?.let { "${it.name} · ${it.width}×${it.height}" } ?: "Display ${display + 1}")
                    Spacer(Modifier.weight(1f))
                    badge()
                }
            }
        }
        if (displays.size > 1) {
            LazyRow(horizontalArrangement = Arrangement.spacedBy(8.dp), contentPadding = PaddingValues(top = 8.dp)) {
                items(displays, key = { it.index }) { d ->
                    FilterChip(
                        selected = d.index == display,
                        onClick = { display = d.index },
                        label = { Text(d.name + if (d.primary) " (main)" else "") },
                    )
                }
            }
        }
    }

    if (fullscreen && frame?.bitmap != null) {
        Dialog(onDismissRequest = { fullscreen = false }, properties = DialogProperties(usePlatformDefaultWidth = false)) {
            Box(
                Modifier.fillMaxSize().background(Color.Black).clickable(onClickLabel = "Close") { fullscreen = false },
                contentAlignment = Alignment.Center,
            ) {
                Image(
                    bitmap = frame.bitmap.asImageBitmap(),
                    contentDescription = "Live output, full screen",
                    contentScale = ContentScale.Fit,
                    modifier = Modifier.fillMaxWidth(),
                )
                Box(Modifier.align(Alignment.TopEnd).padding(16.dp)) { badge() }
                Text(
                    "Tap to close",
                    style = MaterialTheme.typography.labelMedium,
                    color = Color.White.copy(alpha = 0.7f),
                    modifier = Modifier.align(Alignment.BottomCenter).navigationBarsPadding().padding(16.dp),
                )
            }
        }
    }
}

@Composable
private fun WatchOnlyBanner(viaHub: Boolean) {
    val c = ShowTheme.colors
    Surface(shape = RoundedCornerShape(14.dp), color = c.card, border = BorderStroke(1.dp, c.cardBorder), modifier = Modifier.fillMaxWidth().padding(top = 8.dp)) {
        Row(Modifier.padding(14.dp), verticalAlignment = Alignment.CenterVertically) {
            Icon(Icons.Rounded.Visibility, contentDescription = null, tint = c.accentText, modifier = Modifier.size(20.dp))
            Spacer(Modifier.width(12.dp))
            Column {
                Text("Watch only", style = MaterialTheme.typography.titleSmall)
                Text(
                    if (viaHub) "Your hub account can see this PC but not start or stop anything."
                    else "This phone is set to watch only on this PC (agent dashboard → Phones).",
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
        }
    }
}

private fun ScreenshotFrame.aspect(): Float? = if (width > 0 && height > 0) width.toFloat() / height else null

@Composable
private fun OverlayLabel(text: String, live: Boolean = false, warn: Boolean = false) {
    val bg = when {
        live -> Color(0xFFD63031).copy(alpha = 0.88f)
        warn -> Color(0xFF8A5A00).copy(alpha = 0.85f)
        else -> Color.Black.copy(alpha = 0.55f)
    }
    Text(
        text,
        style = MaterialTheme.typography.labelSmall,
        color = Color.White,
        maxLines = 1,
        overflow = TextOverflow.Ellipsis,
        modifier = Modifier.clip(RoundedCornerShape(6.dp)).background(bg).padding(horizontal = 8.dp, vertical = 4.dp),
    )
}
