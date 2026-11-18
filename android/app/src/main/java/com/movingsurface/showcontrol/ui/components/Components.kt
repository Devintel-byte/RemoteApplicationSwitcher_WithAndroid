package com.movingsurface.showcontrol.ui.components

import androidx.compose.animation.core.Animatable
import androidx.compose.animation.core.LinearEasing
import androidx.compose.animation.core.tween
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.rounded.Stop
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.State
import androidx.compose.runtime.produceState
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.hapticfeedback.HapticFeedbackType
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.platform.LocalHapticFeedback
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.onClick
import androidx.compose.ui.semantics.role
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import com.movingsurface.showcontrol.data.AgentLiveState
import com.movingsurface.showcontrol.data.AppState
import com.movingsurface.showcontrol.data.LinkStatus
import com.movingsurface.showcontrol.ui.theme.ShowTheme
import java.time.Duration
import java.time.Instant
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch

@Composable
fun StatusDot(color: Color, size: Dp = 8.dp) {
    Box(Modifier.size(size).clip(CircleShape).background(color))
}

@Composable
fun StatusPill(text: String, fg: Color, bg: Color, modifier: Modifier = Modifier) {
    Row(
        modifier = modifier
            .clip(RoundedCornerShape(50))
            .background(bg)
            .padding(horizontal = 10.dp, vertical = 4.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(6.dp),
    ) {
        StatusDot(fg, 7.dp)
        Text(text, style = MaterialTheme.typography.labelMedium, color = fg)
    }
}

/** Live/offline/needs-pairing pill for one PC. */
@Composable
fun LinkPill(state: AgentLiveState?) {
    val c = ShowTheme.colors
    when {
        state == null || state.status == LinkStatus.CONNECTING -> StatusPill("Connecting", c.warn, c.warnContainer)
        state.status == LinkStatus.UNAUTHORIZED -> StatusPill("Pair again", c.danger, c.dangerContainer)
        state.status == LinkStatus.OFFLINE -> StatusPill("Offline", c.idle, MaterialTheme.colorScheme.surfaceVariant)
        state.crashedCount > 0 -> StatusPill("${state.crashedCount} crashed", c.danger, c.dangerContainer)
        else -> StatusPill("Online", c.ok, c.okContainer)
    }
}

@Composable
fun appStateColor(state: AppState): Color {
    val c = ShowTheme.colors
    return when (state) {
        AppState.RUNNING -> c.ok
        AppState.CRASHED -> c.danger
        AppState.STARTING, AppState.STOPPING -> c.warn
        AppState.STOPPED -> c.idle
    }
}

@Composable
fun linkColor(state: AgentLiveState?): Color {
    val c = ShowTheme.colors
    return when {
        state == null || state.status == LinkStatus.CONNECTING -> c.warn
        state.status != LinkStatus.ONLINE -> c.idle
        state.crashedCount > 0 -> c.danger
        else -> c.ok
    }
}

@Composable
fun SectionHeader(title: String, modifier: Modifier = Modifier, trailing: @Composable (() -> Unit)? = null) {
    Row(
        modifier = modifier.fillMaxWidth().padding(top = 20.dp, bottom = 10.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Text(title, style = MaterialTheme.typography.titleSmall, modifier = Modifier.weight(1f))
        trailing?.invoke()
    }
}

/**
 * Press and hold to fire. Used for destructive actions (stop all) so a stray tap in a dark
 * venue never ends a show.
 */
@Composable
fun HoldToConfirmButton(
    text: String,
    onConfirmed: () -> Unit,
    modifier: Modifier = Modifier,
    holdMillis: Int = 1200,
    icon: ImageVector = Icons.Rounded.Stop,
    enabled: Boolean = true,
) {
    val c = ShowTheme.colors
    val progress = remember { Animatable(0f) }
    val scope = rememberCoroutineScope()
    val haptics = LocalHapticFeedback.current
    val shape = RoundedCornerShape(16.dp)

    Box(
        modifier = modifier
            .fillMaxWidth()
            .height(56.dp)
            .clip(shape)
            .background(c.dangerContainer)
            .border(1.dp, c.dangerBorder, shape)
            .semantics {
                role = Role.Button
                contentDescription = "$text. Press and hold."
                onClick(label = text) { if (enabled) onConfirmed(); true }
            }
            .pointerInput(enabled) {
                if (!enabled) return@pointerInput
                detectTapGestures(onPress = {
                    haptics.performHapticFeedback(HapticFeedbackType.TextHandleMove)
                    val job = scope.launch {
                        progress.animateTo(1f, tween(holdMillis, easing = LinearEasing))
                        haptics.performHapticFeedback(HapticFeedbackType.LongPress)
                        onConfirmed()
                        progress.snapTo(0f)
                    }
                    tryAwaitRelease()
                    if (job.isActive) {
                        job.cancel()
                        scope.launch { progress.animateTo(0f, tween(200)) }
                    }
                })
            },
        contentAlignment = Alignment.Center,
    ) {
        Box(
            Modifier
                .align(Alignment.CenterStart)
                .fillMaxHeight()
                .fillMaxWidth(progress.value)
                .background(c.danger.copy(alpha = 0.22f))
        )
        Row(verticalAlignment = Alignment.CenterVertically) {
            Icon(icon, contentDescription = null, tint = c.danger, modifier = Modifier.size(20.dp))
            Spacer(Modifier.width(10.dp))
            Text(
                if (progress.value > 0f) "Keep holding…" else text,
                color = c.danger,
                style = MaterialTheme.typography.labelLarge,
                fontWeight = FontWeight.SemiBold,
            )
        }
    }
}

@Composable
fun ConfirmDialog(
    title: String,
    text: String,
    confirmLabel: String,
    onConfirm: () -> Unit,
    onDismiss: () -> Unit,
    destructive: Boolean = false,
) {
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text(title) },
        text = { Text(text) },
        confirmButton = {
            TextButton(onClick = { onConfirm(); onDismiss() }) {
                Text(confirmLabel, color = if (destructive) ShowTheme.colors.danger else ShowTheme.colors.accentText)
            }
        },
        dismissButton = { TextButton(onClick = onDismiss) { Text("Cancel") } },
    )
}

/** Ticks once a second so "running 01:42" stays live. */
@Composable
fun rememberNow(): State<Instant> = produceState(Instant.now()) {
    while (true) {
        delay(1_000)
        value = Instant.now()
    }
}

fun formatElapsed(since: String?, now: Instant): String? {
    val start = runCatching { Instant.parse(since) }.getOrNull() ?: return null
    val secs = Duration.between(start, now).seconds.coerceAtLeast(0)
    val h = secs / 3600
    val m = (secs % 3600) / 60
    val s = secs % 60
    return if (h > 0) "%d:%02d:%02d".format(h, m, s) else "%02d:%02d".format(m, s)
}

fun formatUptime(sec: Long): String {
    val d = sec / 86_400
    val h = (sec % 86_400) / 3600
    val m = (sec % 3600) / 60
    return when {
        d > 0 -> "${d}d ${h}h"
        h > 0 -> "${h}h ${m}m"
        else -> "${m}m"
    }
}

fun appStateLabel(state: AppState): String = when (state) {
    AppState.RUNNING -> "running"
    AppState.STARTING -> "starting…"
    AppState.STOPPING -> "closing…"
    AppState.STOPPED -> "stopped"
    AppState.CRASHED -> "crashed"
}

/** Re-runs [block] whenever [key] changes; handy for one-shot snackbar messages. */
@Composable
fun <T> OnChange(key: T, block: suspend (T) -> Unit) {
    LaunchedEffect(key) { block(key) }
}
