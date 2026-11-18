package com.movingsurface.showcontrol.ui.screens

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
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.rounded.ArrowBack
import androidx.compose.material.icons.rounded.Computer
import androidx.compose.material.icons.rounded.Refresh
import androidx.compose.material3.Button
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
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
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.movingsurface.showcontrol.data.AppSettings
import com.movingsurface.showcontrol.ui.MainViewModel
import com.movingsurface.showcontrol.ui.components.SectionHeader
import com.movingsurface.showcontrol.ui.components.StatusDot
import com.movingsurface.showcontrol.ui.theme.ShowTheme

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun AddPcScreen(vm: MainViewModel, settings: AppSettings, roomId: String, onBack: () -> Unit) {
    val room = settings.rooms.firstOrNull { it.id == roomId }
    val discovery by vm.discovery.collectAsStateWithLifecycle()
    val pair by vm.pair.collectAsStateWithLifecycle()
    val c = ShowTheme.colors

    var host by rememberSaveable { mutableStateOf("") }
    var port by rememberSaveable { mutableStateOf("47800") }
    var name by rememberSaveable { mutableStateOf("") }
    var pin by rememberSaveable { mutableStateOf("") }

    LaunchedEffect(Unit) {
        vm.resetAddPc()
        vm.scan()
    }

    val portNum = port.toIntOrNull()?.takeIf { it in 1..65535 }
    val canPair = host.isNotBlank() && portNum != null && pin.length == 6 && !pair.busy

    Scaffold(
        containerColor = MaterialTheme.colorScheme.background,
        topBar = {
            TopAppBar(
                title = {
                    Column {
                        Text("Add a PC", style = MaterialTheme.typography.titleLarge)
                        room?.let { Text("to ${it.name}", style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.onSurfaceVariant) }
                    }
                },
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
            if (settings.hub != null) HubPcsSection(vm, settings, roomId, onAdded = onBack)

            SectionHeader("On this network") {
                if (discovery.scanning) CircularProgressIndicator(strokeWidth = 2.dp, modifier = Modifier.size(18.dp))
                else TextButton(onClick = { vm.scan() }) {
                    Icon(Icons.Rounded.Refresh, contentDescription = null, tint = c.accentText, modifier = Modifier.size(18.dp))
                    Spacer(Modifier.width(6.dp))
                    Text("Scan again", color = c.accentText)
                }
            }

            if (discovery.results.isEmpty()) {
                Text(
                    when {
                        discovery.scanning -> "Looking for PCs running Show Control Agent…"
                        discovery.scannedOnce -> "No new PCs answered. Check the PC is on the same Wi-Fi/LAN and the agent is in its tray, or type its IP address below."
                        else -> ""
                    },
                    style = MaterialTheme.typography.bodyMedium,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }

            Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                discovery.results.forEach { agent ->
                    val selected = agent.host == host && agent.port.toString() == port
                    Surface(
                        onClick = { host = agent.host; port = agent.port.toString(); name = agent.name },
                        shape = RoundedCornerShape(14.dp),
                        color = c.card,
                        border = BorderStroke(if (selected) 2.dp else 1.dp, if (selected) MaterialTheme.colorScheme.primary else c.cardBorder),
                        modifier = Modifier.fillMaxWidth(),
                    ) {
                        Row(Modifier.padding(14.dp), verticalAlignment = Alignment.CenterVertically) {
                            Icon(Icons.Rounded.Computer, contentDescription = null, tint = c.accentText)
                            Spacer(Modifier.width(12.dp))
                            Column(Modifier.weight(1f)) {
                                Text(agent.name, style = MaterialTheme.typography.bodyLarge)
                                Text(
                                    "${agent.host}:${agent.port}" + (agent.hostname.takeIf { it.isNotBlank() }?.let { " · $it" } ?: ""),
                                    style = MaterialTheme.typography.labelSmall,
                                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                                )
                            }
                            if (selected) Text("Selected", style = MaterialTheme.typography.labelMedium, color = c.accentText)
                        }
                    }
                }
            }

            SectionHeader("Address")
            Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                OutlinedTextField(
                    value = host,
                    onValueChange = { host = it.trim() },
                    label = { Text("IP address") },
                    placeholder = { Text("192.168.1.20") },
                    singleLine = true,
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Uri, imeAction = ImeAction.Next),
                    modifier = Modifier.weight(1f),
                )
                OutlinedTextField(
                    value = port,
                    onValueChange = { v -> port = v.filter { it.isDigit() }.take(5) },
                    label = { Text("Port") },
                    singleLine = true,
                    isError = portNum == null,
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number, imeAction = ImeAction.Next),
                    modifier = Modifier.width(110.dp),
                )
            }
            Spacer(Modifier.height(10.dp))
            OutlinedTextField(
                value = name,
                onValueChange = { name = it.take(40) },
                label = { Text("Name on this phone (optional)") },
                placeholder = { Text("Uses the PC's own name") },
                singleLine = true,
                modifier = Modifier.fillMaxWidth(),
            )

            SectionHeader("Pairing PIN")
            OutlinedTextField(
                value = pin,
                onValueChange = { v -> pin = v.filter { it.isDigit() }.take(6) },
                label = { Text("6-digit PIN") },
                supportingText = { Text("Right-click Show Control Agent in the PC's tray to see it.") },
                singleLine = true,
                keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.NumberPassword, imeAction = ImeAction.Done),
                modifier = Modifier.fillMaxWidth(),
            )

            pair.error?.let {
                Spacer(Modifier.height(8.dp))
                Text(it, color = c.danger, style = MaterialTheme.typography.bodyMedium)
            }

            Spacer(Modifier.height(20.dp))
            Button(
                onClick = { portNum?.let { vm.pairPc(roomId, host, it, pin, name, onDone = onBack) } },
                enabled = canPair,
                modifier = Modifier.fillMaxWidth().height(52.dp),
            ) {
                if (pair.busy) {
                    CircularProgressIndicator(strokeWidth = 2.dp, color = MaterialTheme.colorScheme.onPrimary, modifier = Modifier.size(20.dp))
                    Spacer(Modifier.width(10.dp))
                    Text("Pairing…")
                } else {
                    Text("Pair and add")
                }
            }
        }
    }
}

/** PCs enrolled on the hub this phone is signed in to: added with one tap, no PIN needed. */
@Composable
private fun HubPcsSection(vm: MainViewModel, settings: AppSettings, roomId: String, onAdded: () -> Unit) {
    val hubUi by vm.hub.collectAsStateWithLifecycle()
    val c = ShowTheme.colors
    LaunchedEffect(Unit) { vm.refreshHubAgents() }

    SectionHeader("From your hub" + (settings.hub?.workspace?.takeIf { it.isNotBlank() }?.let { " · $it" } ?: "")) {
        if (hubUi.loadingAgents) CircularProgressIndicator(strokeWidth = 2.dp, modifier = Modifier.size(18.dp))
    }
    hubUi.agentsError?.let { Text(it, color = c.danger, style = MaterialTheme.typography.bodyMedium) }
    if (!hubUi.loadingAgents && hubUi.agentsError == null && hubUi.agents.isEmpty()) {
        Text("No PCs are enrolled on the hub yet.", style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
    }
    Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
        hubUi.agents.forEach { a ->
            val added = settings.pcs.any { it.hubAgentId == a.agentId && it.roomId == roomId }
            Surface(shape = RoundedCornerShape(14.dp), color = c.card, border = BorderStroke(1.dp, c.cardBorder), modifier = Modifier.fillMaxWidth()) {
                Row(Modifier.padding(start = 14.dp, end = 6.dp, top = 10.dp, bottom = 10.dp), verticalAlignment = Alignment.CenterVertically) {
                    StatusDot(if (!a.online) c.idle else if (a.crashed > 0) c.danger else c.ok)
                    Spacer(Modifier.width(12.dp))
                    Column(Modifier.weight(1f)) {
                        Text(a.name, style = MaterialTheme.typography.bodyLarge)
                        Text(hubAgentSubtitle(a), style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                    }
                    if (added) Text("In this room", style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.padding(end = 8.dp))
                    else TextButton(onClick = { vm.addHubPc(roomId, a); onAdded() }) { Text("Add", color = c.accentText) }
                }
            }
        }
    }
}
