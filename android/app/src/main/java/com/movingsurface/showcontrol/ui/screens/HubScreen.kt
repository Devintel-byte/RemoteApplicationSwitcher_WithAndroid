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
import androidx.compose.material.icons.rounded.Refresh
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
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalUriHandler
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.movingsurface.showcontrol.data.AppSettings
import com.movingsurface.showcontrol.data.HubAgent
import com.movingsurface.showcontrol.ui.MainViewModel
import com.movingsurface.showcontrol.ui.components.ConfirmDialog
import com.movingsurface.showcontrol.ui.components.SectionHeader
import com.movingsurface.showcontrol.ui.components.StatusDot
import com.movingsurface.showcontrol.ui.theme.ShowTheme

/** Sign in to a Show Control hub so PCs at other venues can be controlled over the internet. */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun HubScreen(vm: MainViewModel, settings: AppSettings, onBack: () -> Unit) {
    val hubUi by vm.hub.collectAsStateWithLifecycle()
    val account = settings.hub

    Scaffold(
        containerColor = MaterialTheme.colorScheme.background,
        topBar = {
            TopAppBar(
                title = { Text("Remote access", style = MaterialTheme.typography.titleLarge) },
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
            if (account == null) SignInForm(vm, hubUi.busy, hubUi.error)
            else SignedIn(vm, settings, hubUi.agents, hubUi.loadingAgents, hubUi.agentsError)
        }
    }
}

@Composable
private fun SignInForm(vm: MainViewModel, busy: Boolean, error: String?) {
    val c = ShowTheme.colors
    var address by rememberSaveable { mutableStateOf("") }
    var username by rememberSaveable { mutableStateOf("") }
    var password by remember { mutableStateOf("") }
    val canSubmit = address.isNotBlank() && username.isNotBlank() && password.isNotEmpty() && !busy

    Text(
        "A hub lets this phone control PCs at other venues over the internet, with your company's accounts and branding. " +
            "PCs on the same network as this phone don't need it.",
        style = MaterialTheme.typography.bodyMedium,
        color = MaterialTheme.colorScheme.onSurfaceVariant,
        modifier = Modifier.padding(top = 8.dp),
    )
    SectionHeader("Sign in to your hub")
    Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
        OutlinedTextField(
            value = address,
            onValueChange = { address = it.trim() },
            label = { Text("Hub address") },
            placeholder = { Text("hub.yourcompany.com") },
            singleLine = true,
            keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Uri, imeAction = ImeAction.Next),
            modifier = Modifier.fillMaxWidth(),
        )
        OutlinedTextField(
            value = username,
            onValueChange = { username = it.trim() },
            label = { Text("Username") },
            singleLine = true,
            keyboardOptions = KeyboardOptions(imeAction = ImeAction.Next),
            modifier = Modifier.fillMaxWidth(),
        )
        OutlinedTextField(
            value = password,
            onValueChange = { password = it },
            label = { Text("Password") },
            singleLine = true,
            visualTransformation = PasswordVisualTransformation(),
            keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Password, imeAction = ImeAction.Done),
            modifier = Modifier.fillMaxWidth(),
        )
        error?.let { Text(it, color = c.danger, style = MaterialTheme.typography.bodyMedium) }
        Button(
            onClick = { vm.hubSignIn(address, username, password) { password = "" } },
            enabled = canSubmit,
            modifier = Modifier.fillMaxWidth().height(52.dp),
        ) {
            if (busy) {
                CircularProgressIndicator(strokeWidth = 2.dp, color = MaterialTheme.colorScheme.onPrimary, modifier = Modifier.size(20.dp))
                Spacer(Modifier.width(10.dp))
                Text("Signing in…")
            } else {
                Text("Sign in")
            }
        }
        Text(
            "Your hub admin creates your account on the hub's People page.",
            style = MaterialTheme.typography.bodySmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
        )
    }
}

@Composable
private fun SignedIn(vm: MainViewModel, settings: AppSettings, agents: List<HubAgent>, loading: Boolean, error: String?) {
    val c = ShowTheme.colors
    val account = settings.hub ?: return
    var confirmSignOut by remember { mutableStateOf(false) }
    val hubPcCount = settings.pcs.count { it.viaHub }

    val uri = LocalUriHandler.current
    val guide = account.role == "viewer"
    LaunchedEffect(account.token) { if (!guide) vm.refreshHubAgents() }

    SectionHeader("Signed in")
    Surface(shape = RoundedCornerShape(16.dp), color = c.card, border = BorderStroke(1.dp, c.cardBorder), modifier = Modifier.fillMaxWidth()) {
        Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(6.dp)) {
            Text(account.workspace.ifBlank { "Show Control hub" }, style = MaterialTheme.typography.titleMedium)
            Text(account.url, style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
            Text(
                "${account.displayName.ifBlank { account.username }} · ${roleLabel(account.role)}",
                style = MaterialTheme.typography.bodyMedium,
            )
            if (guide) {
                Text(
                    "Tour guide accounts use the kiosks assigned to them: pick a PC, then a kiosk, and switch what plays, full screen.",
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
                Button(onClick = { uri.openUri(account.url.trimEnd('/') + "/kiosk") }, modifier = Modifier.padding(top = 6.dp)) { Text("Open my kiosks") }
            }
            Row(horizontalArrangement = Arrangement.spacedBy(10.dp), modifier = Modifier.padding(top = 6.dp)) {
                OutlinedButton(onClick = vm::refreshHubBranding) { Text("Refresh branding") }
                TextButton(onClick = { confirmSignOut = true }) { Text("Sign out", color = c.danger) }
            }
        }
    }

    if (guide) {
        if (confirmSignOut) {
            ConfirmDialog(
                title = "Sign out of ${account.workspace.ifBlank { "the hub" }}?",
                text = "You can sign in again any time.",
                confirmLabel = "Sign out",
                destructive = true,
                onConfirm = vm::hubSignOut,
                onDismiss = { confirmSignOut = false },
            )
        }
        return
    }

    SectionHeader("PCs on this hub") {
        if (loading) CircularProgressIndicator(strokeWidth = 2.dp, modifier = Modifier.size(18.dp))
        else IconButton(onClick = vm::refreshHubAgents) { Icon(Icons.Rounded.Refresh, contentDescription = "Refresh", tint = c.accentText) }
    }
    error?.let { Text(it, color = c.danger, style = MaterialTheme.typography.bodyMedium, modifier = Modifier.padding(bottom = 8.dp)) }
    if (!loading && error == null && agents.isEmpty()) {
        Text("No PCs are enrolled on this hub yet. Connect one from its agent dashboard (Hub page).", style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
    }
    agents.forEach { a ->
        Column {
            Row(Modifier.fillMaxWidth().padding(vertical = 10.dp), verticalAlignment = Alignment.CenterVertically) {
                StatusDot(if (!a.online) c.idle else if (a.crashed > 0) c.danger else c.ok)
                Spacer(Modifier.width(12.dp))
                Column(Modifier.weight(1f)) {
                    Text(a.name, style = MaterialTheme.typography.bodyLarge)
                    Text(hubAgentSubtitle(a), style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                }
                val rooms = settings.pcs.filter { it.hubAgentId == a.agentId }.mapNotNull { pc -> settings.rooms.firstOrNull { it.id == pc.roomId }?.name }
                if (rooms.isNotEmpty()) Text(rooms.joinToString(), style = MaterialTheme.typography.labelMedium, color = c.accentText)
            }
            HorizontalDivider(color = MaterialTheme.colorScheme.outlineVariant)
        }
    }
    if (agents.isNotEmpty()) {
        Text(
            "To control a PC, open a room, tap + and choose it under \"From your hub\".",
            style = MaterialTheme.typography.bodySmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
            modifier = Modifier.padding(top = 12.dp),
        )
    }

    if (confirmSignOut) {
        ConfirmDialog(
            title = "Sign out of ${account.workspace.ifBlank { "the hub" }}?",
            text = if (hubPcCount > 0) "The $hubPcCount ${if (hubPcCount == 1) "PC" else "PCs"} you reach through the hub will be removed from this phone. PCs on your local network stay."
            else "You can sign in again any time.",
            confirmLabel = "Sign out",
            destructive = true,
            onConfirm = vm::hubSignOut,
            onDismiss = { confirmSignOut = false },
        )
    }
}

fun hubAgentSubtitle(a: HubAgent): String = when {
    !a.online -> "Offline" + (if (a.canWake) " · can be woken" else "")
    a.crashed > 0 -> "${a.crashed} crashed · ${a.running} of ${a.apps} apps running"
    else -> "Online · ${a.running} of ${a.apps} apps running"
}

private fun roleLabel(role: String) = when (role) {
    "admin" -> "Admin"
    "viewer" -> "Viewer (tour guide)"
    else -> "Operator"
}
