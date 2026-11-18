package com.movingsurface.showcontrol.ui.components

import androidx.compose.animation.core.Animatable
import androidx.compose.animation.core.LinearEasing
import androidx.compose.animation.core.tween
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.defaultMinSize
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.rounded.ArrowBack
import androidx.compose.material.icons.automirrored.rounded.ArrowForward
import androidx.compose.material.icons.rounded.Add
import androidx.compose.material.icons.rounded.AutoAwesome
import androidx.compose.material.icons.rounded.CheckCircle
import androidx.compose.material.icons.rounded.Edit
import androidx.compose.material.icons.rounded.ErrorOutline
import androidx.compose.material.icons.rounded.GraphicEq
import androidx.compose.material.icons.rounded.Lightbulb
import androidx.compose.material.icons.rounded.MeetingRoom
import androidx.compose.material.icons.rounded.Movie
import androidx.compose.material.icons.rounded.MusicNote
import androidx.compose.material.icons.rounded.PanTool
import androidx.compose.material.icons.rounded.PhotoCamera
import androidx.compose.material.icons.rounded.PlayArrow
import androidx.compose.material.icons.rounded.PowerSettingsNew
import androidx.compose.material.icons.rounded.Refresh
import androidx.compose.material.icons.rounded.SportsEsports
import androidx.compose.material.icons.rounded.Star
import androidx.compose.material.icons.rounded.Stop
import androidx.compose.material.icons.rounded.Timer
import androidx.compose.material.icons.rounded.TouchApp
import androidx.compose.material.icons.rounded.Videocam
import androidx.compose.material.icons.rounded.Visibility
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.hapticfeedback.HapticFeedbackType
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.platform.LocalHapticFeedback
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.onClick
import androidx.compose.ui.semantics.role
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import com.movingsurface.showcontrol.data.SafetyMode
import com.movingsurface.showcontrol.ui.theme.ShowTheme
import com.movingsurface.showcontrol.ui.theme.labelColors
import kotlinx.coroutines.launch

/** Icons a tile can use. Agent scenes can name these too ("icon": "film" in agent.json). */
val TileIconKeys = listOf(
    "play", "film", "videocam", "camera", "music", "light", "sparkle", "gamepad",
    "refresh", "timer", "door", "power", "stop", "star", "wave",
)

fun tileIcon(key: String?): ImageVector = when (key) {
    "film" -> Icons.Rounded.Movie
    "videocam" -> Icons.Rounded.Videocam
    "camera" -> Icons.Rounded.PhotoCamera
    "music" -> Icons.Rounded.MusicNote
    "light" -> Icons.Rounded.Lightbulb
    "sparkle" -> Icons.Rounded.AutoAwesome
    "gamepad" -> Icons.Rounded.SportsEsports
    "refresh" -> Icons.Rounded.Refresh
    "timer" -> Icons.Rounded.Timer
    "door" -> Icons.Rounded.MeetingRoom
    "power" -> Icons.Rounded.PowerSettingsNew
    "stop" -> Icons.Rounded.Stop
    "star" -> Icons.Rounded.Star
    "wave" -> Icons.Rounded.GraphicEq
    else -> Icons.Rounded.PlayArrow
}

enum class TileStatus { IDLE, RUNNING, DONE, FAILED }

/**
 * A scene tile. Firing respects [safety]: Tap fires at once, Confirm asks first, Hold needs a
 * ~1 s press (with a fill showing progress). In [editMode] a tap opens the editor instead.
 * A [readOnly] tile (watch-only phone) shows status but can't be fired.
 */
@Composable
fun SceneTile(
    title: String,
    subtitle: String,
    colorKey: String?,
    iconKey: String?,
    safety: SafetyMode,
    status: TileStatus,
    progress: Float?,
    onFire: () -> Unit,
    modifier: Modifier = Modifier,
    editMode: Boolean = false,
    onEdit: (() -> Unit)? = null,
    onMoveEarlier: (() -> Unit)? = null,
    onMoveLater: (() -> Unit)? = null,
    holdMillis: Int = 1000,
    readOnly: Boolean = false,
) {
    val label = labelColors(colorKey, ShowTheme.isDark)
    val shape = RoundedCornerShape(16.dp)
    val hold = remember { Animatable(0f) }
    val scope = rememberCoroutineScope()
    val haptics = LocalHapticFeedback.current
    var confirming by remember { mutableStateOf(false) }

    val interaction: Modifier = when {
        readOnly && !editMode -> Modifier.semantics { contentDescription = "$title. $subtitle. View only." }
        editMode && onEdit != null -> Modifier.clickable(role = Role.Button, onClickLabel = "Edit $title") { onEdit() }
        safety == SafetyMode.TAP -> Modifier.clickable(role = Role.Button, onClickLabel = "Run $title") {
            haptics.performHapticFeedback(HapticFeedbackType.LongPress); onFire()
        }
        safety == SafetyMode.CONFIRM -> Modifier.clickable(role = Role.Button, onClickLabel = "Run $title") { confirming = true }
        else -> Modifier
            .semantics {
                role = Role.Button
                contentDescription = "$title. Press and hold to run."
                onClick(label = "Run $title") { confirming = true; true }
            }
            .pointerInput(title) {
                detectTapGestures(onPress = {
                    val job = scope.launch {
                        hold.animateTo(1f, tween(holdMillis, easing = LinearEasing))
                        haptics.performHapticFeedback(HapticFeedbackType.LongPress)
                        onFire()
                        hold.snapTo(0f)
                    }
                    tryAwaitRelease()
                    if (job.isActive) {
                        job.cancel()
                        scope.launch { hold.animateTo(0f, tween(180)) }
                    }
                })
            }
    }

    Box(
        modifier
            .clip(shape)
            .background(label.bg)
            .border(if (editMode) 2.dp else 1.dp, if (editMode) label.fg else label.border, shape)
            .then(interaction),
    ) {
        if (hold.value > 0f) {
            Box(Modifier.matchParentSize()) {
                Box(Modifier.fillMaxHeight().fillMaxWidth(hold.value).background(label.fg.copy(alpha = 0.22f)))
            }
        }
        Column(Modifier.defaultMinSize(minHeight = 112.dp).padding(14.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Box(Modifier.size(24.dp), contentAlignment = Alignment.Center) {
                    when (status) {
                        TileStatus.RUNNING -> CircularProgressIndicator(color = label.fg, strokeWidth = 2.dp, modifier = Modifier.size(20.dp))
                        TileStatus.DONE -> Icon(Icons.Rounded.CheckCircle, contentDescription = "Done", tint = label.fg)
                        TileStatus.FAILED -> Icon(Icons.Rounded.ErrorOutline, contentDescription = "Failed", tint = label.fg)
                        TileStatus.IDLE -> Icon(tileIcon(iconKey), contentDescription = null, tint = label.fg)
                    }
                }
                Spacer(Modifier.weight(1f))
                when {
                    editMode -> Icon(Icons.Rounded.Edit, contentDescription = null, tint = label.sub, modifier = Modifier.size(16.dp))
                    readOnly -> SafetyBadge(Icons.Rounded.Visibility, "View only", label.sub)
                    safety == SafetyMode.HOLD -> SafetyBadge(Icons.Rounded.PanTool, if (hold.value > 0f) "Holding…" else "Hold", label.sub)
                    safety == SafetyMode.CONFIRM -> SafetyBadge(Icons.Rounded.TouchApp, "Confirm", label.sub)
                }
            }
            Spacer(Modifier.height(16.dp))
            Text(title, style = MaterialTheme.typography.titleSmall, color = MaterialTheme.colorScheme.onSurface, maxLines = 2, overflow = TextOverflow.Ellipsis)
            Spacer(Modifier.height(3.dp))
            Text(subtitle, style = MaterialTheme.typography.bodySmall, color = label.sub, maxLines = 2, overflow = TextOverflow.Ellipsis)
            if (status == TileStatus.RUNNING && progress != null) {
                Spacer(Modifier.height(8.dp))
                LinearProgressIndicator(progress = { progress.coerceIn(0f, 1f) }, color = label.fg, trackColor = label.border, modifier = Modifier.fillMaxWidth())
            }
            if (editMode && (onMoveEarlier != null || onMoveLater != null)) {
                Row(Modifier.fillMaxWidth().padding(top = 4.dp), horizontalArrangement = Arrangement.End) {
                    IconButton(onClick = { onMoveEarlier?.invoke() }, enabled = onMoveEarlier != null) {
                        Icon(Icons.AutoMirrored.Rounded.ArrowBack, contentDescription = "Move earlier", tint = label.fg)
                    }
                    IconButton(onClick = { onMoveLater?.invoke() }, enabled = onMoveLater != null) {
                        Icon(Icons.AutoMirrored.Rounded.ArrowForward, contentDescription = "Move later", tint = label.fg)
                    }
                }
            }
        }
    }

    if (confirming) {
        ConfirmDialog(
            title = "Run $title?",
            text = subtitle,
            confirmLabel = "Run",
            destructive = colorKey == "red",
            onConfirm = onFire,
            onDismiss = { confirming = false },
        )
    }
}

@Composable
private fun SafetyBadge(icon: ImageVector, text: String, tint: androidx.compose.ui.graphics.Color) {
    Row(verticalAlignment = Alignment.CenterVertically) {
        Icon(icon, contentDescription = null, tint = tint, modifier = Modifier.size(14.dp))
        Spacer(Modifier.size(4.dp))
        Text(text, style = MaterialTheme.typography.labelSmall, color = tint)
    }
}

/** Dashed-look "add" tile shown in edit mode and in empty panels. */
@Composable
fun AddTile(text: String, onClick: () -> Unit, modifier: Modifier = Modifier) {
    val shape = RoundedCornerShape(16.dp)
    val c = ShowTheme.colors
    Box(
        modifier
            .defaultMinSize(minHeight = 112.dp)
            .clip(shape)
            .border(1.5.dp, MaterialTheme.colorScheme.outline, shape)
            .clickable(role = Role.Button, onClick = onClick)
            .padding(14.dp),
        contentAlignment = Alignment.Center,
    ) {
        Column(horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.spacedBy(6.dp)) {
            Icon(Icons.Rounded.Add, contentDescription = null, tint = c.accentText)
            Text(text, style = MaterialTheme.typography.labelLarge, color = c.accentText)
        }
    }
}
