package com.movingsurface.showcontrol.ui.screens

import android.content.Intent
import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.rounded.ArrowBack
import androidx.compose.material.icons.rounded.CheckCircle
import androidx.compose.material.icons.rounded.Cloud
import androidx.compose.material.icons.rounded.Key
import androidx.compose.material.icons.rounded.Refresh
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
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
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.platform.LocalClipboardManager
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.AnnotatedString
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.movingsurface.showcontrol.data.AppSettings
import com.movingsurface.showcontrol.data.RoomImport
import com.movingsurface.showcontrol.data.RoomShare
import com.movingsurface.showcontrol.ui.MainViewModel
import com.movingsurface.showcontrol.ui.components.SectionHeader
import com.movingsurface.showcontrol.ui.theme.ShowTheme

/** Send a room's tiles to another phone: as a code through any messenger, or published to the hub. */
@Composable
fun ShareRoomDialog(
    vm: MainViewModel,
    roomName: String,
    tiles: Int,
    pcs: Int,
    hubName: String?,
    roomId: String,
    onDismiss: () -> Unit,
) {
    val context = LocalContext.current
    val clipboard = LocalClipboardManager.current
    fun message(): String {
        val code = vm.shareCode(roomId)
        return "Show Control room \"$roomName\" ($tiles ${if (tiles == 1) "tile" else "tiles"}). " +
            "On the other phone, share this message to Show Control, or copy it and open Rooms → Import.\n\n$code"
    }

    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text("Share $roomName") },
        text = {
            Column(verticalArrangement = Arrangement.spacedBy(12.dp)) {
                Text(
                    "Another phone gets this room's $tiles ${if (tiles == 1) "tile" else "tiles"} and its $pcs ${if (pcs == 1) "PC" else "PCs"}. " +
                        "Pairing keys and passwords are never included: a PC the other phone doesn't know yet asks for its PIN.",
                    style = MaterialTheme.typography.bodyMedium,
                )
                Button(
                    onClick = {
                        val send = Intent(Intent.ACTION_SEND)
                            .setType("text/plain")
                            .putExtra(Intent.EXTRA_SUBJECT, "Show Control room: $roomName")
                            .putExtra(Intent.EXTRA_TEXT, message())
                        context.startActivity(Intent.createChooser(send, "Share $roomName"))
                        onDismiss()
                    },
                    modifier = Modifier.fillMaxWidth(),
                ) { Text("Send as a code") }
                OutlinedButton(
                    onClick = {
                        clipboard.setText(AnnotatedString(message()))
                        vm.notifyUser("Room code copied")
                        onDismiss()
                    },
                    modifier = Modifier.fillMaxWidth(),
                ) { Text("Copy code") }
                if (hubName != null) {
                    OutlinedButton(onClick = { vm.publishRoom(roomId); onDismiss() }, modifier = Modifier.fillMaxWidth()) {
                        Text("Publish to $hubName")
                    }
                    Text(
                        "Everyone signed in to $hubName can then import it, and publishing again updates their copy when they re-import.",
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }
            }
        },
        confirmButton = { TextButton(onClick = onDismiss) { Text("Close") } },
    )
}

/** Import a room someone shared: paste (or receive) a code, or pick one published on the hub. */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun ImportRoomScreen(
    vm: MainViewModel,
    settings: AppSettings,
    incoming: String?,
    onBack: () -> Unit,
    onImported: (roomId: String) -> Unit,
) {
    val ui by vm.importUi.collectAsStateWithLifecycle()
    val clipboard = LocalClipboardManager.current
    var text by rememberSaveable { mutableStateOf("") }

    LaunchedEffect(incoming) {
        if (incoming != null) {
            text = incoming
            vm.previewCode(incoming)
            vm.consumeIncomingCode()
        }
    }
    LaunchedEffect(settings.hub?.token) { if (settings.hub != null) vm.loadHubRooms() }
    DisposableEffect(Unit) { onDispose { vm.clearImportPreview() } }

    Scaffold(
        containerColor = MaterialTheme.colorScheme.background,
        topBar = {
            TopAppBar(
                title = { Text("Import a room", style = MaterialTheme.typography.titleLarge) },
                navigationIcon = { IconButton(onClick = onBack) { Icon(Icons.AutoMirrored.Rounded.ArrowBack, contentDescription = "Back") } },
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
                .padding(bottom = 32.dp),
        ) {
            val preview = ui.preview
            if (preview != null) {
                ImportPreview(preview, onImport = { vm.confirmImport(onImported) }, onCancel = vm::clearImportPreview)
                return@Column
            }

            SectionHeader("From a share code")
            Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
                Text(
                    "Paste the message someone sent you. It starts with ${RoomShare.PREFIX}",
                    style = MaterialTheme.typography.bodyMedium,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
                OutlinedTextField(
                    value = text,
                    onValueChange = { text = it },
                    label = { Text("Room code") },
                    minLines = 3,
                    maxLines = 6,
                    textStyle = MaterialTheme.typography.bodySmall.copy(fontFamily = FontFamily.Monospace),
                    modifier = Modifier.fillMaxWidth(),
                )
                ui.error?.let { Text(it, color = ShowTheme.colors.danger, style = MaterialTheme.typography.bodyMedium) }
                Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                    OutlinedButton(onClick = {
                        val pasted = clipboard.getText()?.text.orEmpty()
                        text = pasted
                        if (RoomShare.containsCode(pasted)) vm.previewCode(pasted)
                    }) { Text("Paste") }
                    Button(onClick = { vm.previewCode(text) }, enabled = text.isNotBlank()) { Text("Read code") }
                }
            }

            val hub = settings.hub
            if (hub != null) {
                SectionHeader("Published on ${hub.workspace.ifBlank { "your hub" }}") {
                    if (ui.loadingHub) CircularProgressIndicator(strokeWidth = 2.dp, modifier = Modifier.size(18.dp))
                    else IconButton(onClick = vm::loadHubRooms) { Icon(Icons.Rounded.Refresh, contentDescription = "Refresh", tint = ShowTheme.colors.accentText) }
                }
                ui.hubError?.let { Text(it, color = ShowTheme.colors.danger, style = MaterialTheme.typography.bodyMedium) }
                if (!ui.loadingHub && ui.hubError == null && ui.hubRooms.isEmpty()) {
                    Text(
                        "Nobody has published a room yet. On a phone that has it: open the room, tap Share → Publish.",
                        style = MaterialTheme.typography.bodyMedium,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }
                ui.hubRooms.forEach { r ->
                    val mine = settings.rooms.any { it.id == r.id }
                    Column {
                        Row(Modifier.fillMaxWidth().padding(vertical = 10.dp), verticalAlignment = Alignment.CenterVertically) {
                            Column(Modifier.weight(1f)) {
                                Text(r.name, style = MaterialTheme.typography.bodyLarge)
                                Text(
                                    "${r.tiles} ${if (r.tiles == 1) "tile" else "tiles"} · ${r.pcs} ${if (r.pcs == 1) "PC" else "PCs"}" +
                                        (if (r.updatedBy.isNotBlank()) " · by ${r.updatedBy}" else ""),
                                    style = MaterialTheme.typography.labelSmall,
                                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                                )
                            }
                            OutlinedButton(onClick = { vm.previewHubRoom(r.id) }, enabled = !ui.busy) { Text(if (mine) "Update" else "Import") }
                        }
                        HorizontalDivider(color = MaterialTheme.colorScheme.outlineVariant)
                    }
                }
            } else {
                Spacer(Modifier.height(20.dp))
                Text(
                    "Signed in to a hub? Rooms your team publishes there show up here too (Settings → Remote access).",
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
        }
    }
}

@Composable
private fun ImportPreview(p: RoomImport, onImport: () -> Unit, onCancel: () -> Unit) {
    val c = ShowTheme.colors
    SectionHeader(if (p.updatesExisting) "Update a room" else "New room")
    Surface(shape = RoundedCornerShape(16.dp), color = c.card, border = BorderStroke(1.dp, c.cardBorder), modifier = Modifier.fillMaxWidth()) {
        Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
            Text(p.roomName, style = MaterialTheme.typography.titleLarge)
            Text(
                "${p.tiles} ${if (p.tiles == 1) "tile" else "tiles"}" +
                    if (p.updatesExisting) " · replaces the tiles you have in this room" else " · added as a new room",
                style = MaterialTheme.typography.bodyMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
            if (p.ready.isNotEmpty()) PcGroup(Icons.Rounded.CheckCircle, c.ok, "Ready on this phone", p.ready)
            if (p.needPin.isNotEmpty()) PcGroup(Icons.Rounded.Key, c.warn, "Needs the PC's pairing PIN (asked for when you open it)", p.needPin)
            if (p.needHub.isNotEmpty()) PcGroup(Icons.Rounded.Cloud, c.warn, "Reached through a hub: sign in to it in Settings → Remote access", p.needHub)
        }
    }
    Row(Modifier.padding(top = 16.dp), horizontalArrangement = Arrangement.spacedBy(10.dp)) {
        Button(onClick = onImport, modifier = Modifier.weight(1f).height(52.dp)) { Text(if (p.updatesExisting) "Update room" else "Import room") }
        OutlinedButton(onClick = onCancel, modifier = Modifier.height(52.dp)) { Text("Cancel") }
    }
}

@Composable
private fun PcGroup(icon: ImageVector, tint: Color, title: String, names: List<String>) {
    Row(verticalAlignment = Alignment.Top) {
        Icon(icon, contentDescription = null, tint = tint, modifier = Modifier.size(18.dp).padding(top = 2.dp))
        Spacer(Modifier.width(10.dp))
        Column {
            Text(title, style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
            Text(names.joinToString(", "), style = MaterialTheme.typography.bodyMedium)
        }
    }
}
