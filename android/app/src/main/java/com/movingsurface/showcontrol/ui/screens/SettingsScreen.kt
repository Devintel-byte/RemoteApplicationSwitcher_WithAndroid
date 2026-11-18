package com.movingsurface.showcontrol.ui.screens

import android.Manifest
import android.content.Intent
import android.os.Build
import android.provider.Settings
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.PickVisualMediaRequest
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.rounded.ArrowBack
import androidx.compose.material.icons.rounded.BrightnessAuto
import androidx.compose.material.icons.rounded.Check
import androidx.compose.material.icons.rounded.DarkMode
import androidx.compose.material.icons.rounded.LightMode
import androidx.compose.material3.Button
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
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
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.selected
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.input.KeyboardCapitalization
import androidx.compose.ui.unit.dp
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.compose.LocalLifecycleOwner
import androidx.lifecycle.repeatOnLifecycle
import com.movingsurface.showcontrol.BuildConfig
import com.movingsurface.showcontrol.ShowControlApp
import com.movingsurface.showcontrol.data.AppSettings
import com.movingsurface.showcontrol.data.LiveViewQuality
import com.movingsurface.showcontrol.data.ThemeMode
import com.movingsurface.showcontrol.ui.MainViewModel
import com.movingsurface.showcontrol.ui.components.BrandMark
import com.movingsurface.showcontrol.ui.components.SectionHeader
import com.movingsurface.showcontrol.ui.theme.Accents
import com.movingsurface.showcontrol.ui.theme.ShowTheme
import kotlinx.coroutines.delay

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun SettingsScreen(vm: MainViewModel, settings: AppSettings, onBack: () -> Unit, onOpenHub: () -> Unit) {
    val c = ShowTheme.colors
    val pickLogo = rememberLauncherForActivityResult(ActivityResultContracts.PickVisualMedia()) { uri ->
        if (uri != null) vm.setLogoFromUri(uri)
    }

    Scaffold(
        containerColor = MaterialTheme.colorScheme.background,
        topBar = {
            TopAppBar(
                title = { Text("Settings", style = MaterialTheme.typography.titleLarge) },
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
            SectionHeader("Appearance")
            SettingsCard {
                Text("Theme", style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
                val options = listOf(
                    Triple(ThemeMode.SYSTEM, "System", Icons.Rounded.BrightnessAuto),
                    Triple(ThemeMode.LIGHT, "Light", Icons.Rounded.LightMode),
                    Triple(ThemeMode.DARK, "Dark", Icons.Rounded.DarkMode),
                )
                SingleChoiceSegmentedButtonRow(Modifier.fillMaxWidth()) {
                    options.forEachIndexed { i, (mode, label, icon) ->
                        SegmentedButton(
                            selected = settings.theme == mode,
                            onClick = { vm.setTheme(mode) },
                            shape = SegmentedButtonDefaults.itemShape(index = i, count = options.size),
                            icon = { Icon(icon, contentDescription = null, modifier = Modifier.size(18.dp)) },
                            colors = SegmentedButtonDefaults.colors(
                                activeContainerColor = MaterialTheme.colorScheme.secondaryContainer,
                                activeContentColor = MaterialTheme.colorScheme.onSecondaryContainer,
                            ),
                        ) { Text(label) }
                    }
                }

                Text("Accent colour", style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
                Row(horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                    Accents.forEach { accent ->
                        val selected = settings.accent == accent.key
                        Box(
                            Modifier
                                .size(44.dp)
                                .clip(CircleShape)
                                .background(accent.base)
                                .border(if (selected) 3.dp else 0.dp, MaterialTheme.colorScheme.onBackground, CircleShape)
                                .semantics {
                                    contentDescription = "${accent.label} accent"
                                    this.selected = selected
                                }
                                .clickable(role = Role.RadioButton) { vm.setAccent(accent.key) },
                            contentAlignment = Alignment.Center,
                        ) {
                            if (selected) Icon(Icons.Rounded.Check, contentDescription = null, tint = accent.onBase, modifier = Modifier.size(20.dp))
                        }
                    }
                }
            }

            SectionHeader("Live view")
            SettingsCard {
                val q = settings.liveView
                SingleChoiceSegmentedButtonRow(Modifier.fillMaxWidth()) {
                    LiveViewQuality.entries.forEachIndexed { i, option ->
                        SegmentedButton(
                            selected = q == option,
                            onClick = { vm.setLiveView(option) },
                            shape = SegmentedButtonDefaults.itemShape(index = i, count = LiveViewQuality.entries.size),
                            colors = SegmentedButtonDefaults.colors(
                                activeContainerColor = MaterialTheme.colorScheme.secondaryContainer,
                                activeContentColor = MaterialTheme.colorScheme.onSecondaryContainer,
                            ),
                        ) { Text(option.label, maxLines = 1) }
                    }
                }
                Text(
                    "Up to ${q.fps} frames a second, ${q.width} px wide (${q.fullWidth} px full screen). " +
                        "Smooth suits the venue network; use Data saver on mobile data through the hub. " +
                        "The live view only runs while a PC's screen is open.",
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }

            SectionHeader("Alerts")
            SettingsCard { AlertsSettings(vm, settings) }

            SectionHeader("Branding")
            SettingsCard {
                Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(14.dp)) {
                    BrandMark(brandName = settings.brandName, logoVersion = settings.logoVersion, size = 56.dp)
                    Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(6.dp)) {
                        Text("Logo", style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
                        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                            OutlinedButton(onClick = {
                                pickLogo.launch(PickVisualMediaRequest(ActivityResultContracts.PickVisualMedia.ImageOnly))
                            }) { Text(if (settings.logoVersion == 0L) "Choose image" else "Change") }
                            if (settings.logoVersion != 0L) TextButton(onClick = vm::removeLogo) { Text("Remove", color = c.danger) }
                        }
                    }
                }
                if (settings.hub != null) {
                    Text(
                        "Signed in to ${settings.hub.workspace.ifBlank { "a hub" }}: its name, logo and colour are applied when you sign in or tap Refresh branding.",
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }
                DebouncedField(
                    initial = settings.brandName,
                    label = "Company or client name",
                    supporting = "Shown at the top of the rooms screen. Without a logo, its initials are used.",
                    onCommit = vm::setBrandName,
                )
            }

            SectionHeader("Remote access")
            SettingsCard {
                val hub = settings.hub
                if (hub == null) {
                    Text(
                        "Control PCs at other venues over the internet by signing in to your Show Control hub.",
                        style = MaterialTheme.typography.bodyMedium,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                    Button(onClick = onOpenHub) { Text("Sign in to a hub") }
                } else {
                    Text(hub.workspace.ifBlank { "Show Control hub" }, style = MaterialTheme.typography.bodyLarge)
                    Text(
                        "${hub.displayName.ifBlank { hub.username }} · ${hub.role} · ${settings.pcs.count { it.viaHub }} PCs via hub",
                        style = MaterialTheme.typography.labelSmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                    OutlinedButton(onClick = onOpenHub) { Text("Manage") }
                }
            }

            SectionHeader("This phone")
            SettingsCard {
                DebouncedField(
                    initial = settings.deviceName,
                    label = "Phone name",
                    placeholder = ShowControlApp.defaultDeviceName(),
                    supporting = "PCs list this name under paired controllers. Applies to new pairings.",
                    onCommit = vm::setDeviceName,
                )
                OutlinedButton(onClick = vm::reconnectAll) { Text("Reconnect all PCs") }
            }

            SectionHeader("About")
            SettingsCard {
                Text("Show Control ${BuildConfig.VERSION_NAME}", style = MaterialTheme.typography.bodyLarge)
                Text(
                    "Protocol v1 · ${settings.rooms.size} rooms · ${settings.pcs.size} PCs",
                    style = MaterialTheme.typography.labelSmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
                Text(
                    "Moving Surface Interactive Media",
                    style = MaterialTheme.typography.bodySmall,
                    color = c.accentText,
                )
            }
        }
    }
}

@Composable
private fun AlertsSettings(vm: MainViewModel, settings: AppSettings) {
    val c = ShowTheme.colors
    val context = LocalContext.current
    val lifecycle = LocalLifecycleOwner.current.lifecycle
    var permitted by remember { mutableStateOf(vm.notificationsPermitted()) }
    // Re-check when coming back from the system settings screen.
    LaunchedEffect(Unit) { lifecycle.repeatOnLifecycle(Lifecycle.State.RESUMED) { permitted = vm.notificationsPermitted() } }
    val ask = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) { granted ->
        permitted = vm.notificationsPermitted()
        if (!granted) openNotificationSettings(context)
    }
    val a = settings.alerts

    if (!permitted && (a.crashes || a.offline)) {
        Surface(shape = RoundedCornerShape(12.dp), color = c.warnContainer, modifier = Modifier.fillMaxWidth()) {
            Column(Modifier.padding(12.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                Text("Notifications are off for Show Control, so alerts can't reach you.", style = MaterialTheme.typography.bodyMedium)
                Button(onClick = {
                    if (Build.VERSION.SDK_INT >= 33) ask.launch(Manifest.permission.POST_NOTIFICATIONS) else openNotificationSettings(context)
                }) { Text("Allow notifications") }
            }
        }
    }
    SwitchRow("App crashes", "When an app on any PC closes unexpectedly. Updates when the watchdog has it running again.", a.crashes) {
        vm.setAlerts(a.copy(crashes = it))
    }
    SwitchRow("PC offline", "When a PC stops answering for 30 seconds, and when it's back.", a.offline) {
        vm.setAlerts(a.copy(offline = it))
    }
    SwitchRow(
        "Keep watching in the background",
        "Alerts arrive even after you close the app. Android shows a small ongoing notification while this is on; it uses a little battery.",
        a.background,
    ) { vm.setAlerts(a.copy(background = it)) }
}

private fun openNotificationSettings(context: android.content.Context) {
    val intent = Intent(Settings.ACTION_APP_NOTIFICATION_SETTINGS)
        .putExtra(Settings.EXTRA_APP_PACKAGE, context.packageName)
        .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
    runCatching { context.startActivity(intent) }
}

@Composable
private fun SwitchRow(title: String, detail: String, checked: Boolean, onChange: (Boolean) -> Unit) {
    Row(verticalAlignment = Alignment.CenterVertically) {
        Column(Modifier.weight(1f).padding(end = 12.dp)) {
            Text(title, style = MaterialTheme.typography.bodyLarge)
            Text(detail, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
        }
        Switch(checked = checked, onCheckedChange = onChange)
    }
}

@Composable
private fun SettingsCard(content: @Composable () -> Unit) {
    val c = ShowTheme.colors
    Surface(shape = RoundedCornerShape(16.dp), color = c.card, border = BorderStroke(1.dp, c.cardBorder), modifier = Modifier.fillMaxWidth()) {
        Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) { content() }
    }
}

/** Text field that saves 500 ms after the last keystroke instead of on every key. */
@Composable
private fun DebouncedField(
    initial: String,
    label: String,
    supporting: String,
    onCommit: (String) -> Unit,
    placeholder: String? = null,
) {
    var value by remember { mutableStateOf(initial) }
    LaunchedEffect(value) {
        if (value == initial) return@LaunchedEffect
        delay(500)
        onCommit(value.trim())
    }
    OutlinedTextField(
        value = value,
        onValueChange = { value = it.take(40) },
        label = { Text(label) },
        placeholder = if (placeholder != null) { { Text(placeholder) } } else null,
        supportingText = { Text(supporting) },
        singleLine = true,
        keyboardOptions = KeyboardOptions(capitalization = KeyboardCapitalization.Words),
        modifier = Modifier.fillMaxWidth(),
    )
}
