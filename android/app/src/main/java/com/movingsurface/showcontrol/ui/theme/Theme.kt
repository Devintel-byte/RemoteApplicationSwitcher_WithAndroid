package com.movingsurface.showcontrol.ui.theme

import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Shapes
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.Immutable
import androidx.compose.runtime.ReadOnlyComposable
import androidx.compose.runtime.staticCompositionLocalOf
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.unit.dp
import com.movingsurface.showcontrol.data.ThemeMode

/** Colours Material 3 has no slot for: status, cards, accent text. */
@Immutable
data class ExtendedColors(
    val ok: Color,
    val okContainer: Color,
    val danger: Color,
    val dangerContainer: Color,
    val dangerBorder: Color,
    val warn: Color,
    val warnContainer: Color,
    val idle: Color,
    val card: Color,
    val cardBorder: Color,
    val inset: Color,
    val accentText: Color,
)

// Neutrals and status colours shared with the PC dashboard and the hub (shared/web/sc-ui.css).
private val DarkExtended = ExtendedColors(
    ok = Color(0xFF3DD6B0), okContainer = Color(0xFF0F2A26),
    danger = Color(0xFFFF7A7A), dangerContainer = Color(0xFF2B1517), dangerBorder = Color(0xFF5A2226),
    warn = Color(0xFFF5B94A), warnContainer = Color(0xFF2E230C),
    idle = Color(0xFF6B7486),
    card = Color(0xFF151922), cardBorder = Color(0xFF252B36), inset = Color(0xFF0A0D12),
    accentText = Color(0xFFA5B4FC),
)

private val LightExtended = ExtendedColors(
    ok = Color(0xFF0B7A61), okContainer = Color(0xFFDDF4EE),
    danger = Color(0xFFB42318), dangerContainer = Color(0xFFFDE7E5), dangerBorder = Color(0xFFF4C3BF),
    warn = Color(0xFF8A5A00), warnContainer = Color(0xFFFFF2D6),
    idle = Color(0xFF838B9B),
    card = Color(0xFFFFFFFF), cardBorder = Color(0xFFE1E4EA), inset = Color(0xFFF0F2F6),
    accentText = Color(0xFF4338CA),
)

val LocalExtendedColors = staticCompositionLocalOf { DarkExtended }
val LocalIsDark = staticCompositionLocalOf { true }
val LocalAccent = staticCompositionLocalOf { Accents.first() }

object ShowTheme {
    val colors: ExtendedColors
        @Composable @ReadOnlyComposable get() = LocalExtendedColors.current
    val isDark: Boolean
        @Composable @ReadOnlyComposable get() = LocalIsDark.current
    val accent: Accent
        @Composable @ReadOnlyComposable get() = LocalAccent.current
}

@Composable
fun resolveDark(mode: ThemeMode): Boolean = when (mode) {
    ThemeMode.SYSTEM -> isSystemInDarkTheme()
    ThemeMode.LIGHT -> false
    ThemeMode.DARK -> true
}

@Composable
fun ShowControlTheme(mode: ThemeMode, accentKey: String, content: @Composable () -> Unit) {
    val dark = resolveDark(mode)
    val accent = accentFor(accentKey)

    val scheme = if (dark) {
        darkColorScheme(
            primary = accent.base, onPrimary = accent.onBase,
            primaryContainer = accent.base, onPrimaryContainer = accent.onBase,
            secondary = Color(0xFFC9CED8), onSecondary = Color(0xFF0E1117),
            secondaryContainer = Color(0xFF252B36), onSecondaryContainer = Color(0xFFE8EBF1),
            background = Color(0xFF0E1117), onBackground = Color(0xFFE8EBF1),
            surface = Color(0xFF0E1117), onSurface = Color(0xFFE8EBF1),
            surfaceVariant = Color(0xFF1C212B), onSurfaceVariant = Color(0xFF9AA3B4),
            surfaceContainerLowest = Color(0xFF0A0D12), surfaceContainerLow = Color(0xFF12161D),
            surfaceContainer = Color(0xFF151922), surfaceContainerHigh = Color(0xFF1C212B),
            surfaceContainerHighest = Color(0xFF232935),
            outline = Color(0xFF343C49), outlineVariant = Color(0xFF252B36),
            error = Color(0xFFFF7A7A), onError = Color(0xFF2B1517),
            errorContainer = Color(0xFF2B1517), onErrorContainer = Color(0xFFFFB4B4),
        )
    } else {
        lightColorScheme(
            primary = accent.base, onPrimary = accent.onBase,
            primaryContainer = accent.base, onPrimaryContainer = accent.onBase,
            secondary = Color(0xFF3F4656), onSecondary = Color.White,
            secondaryContainer = Color(0xFFE4E7ED), onSecondaryContainer = Color(0xFF11141B),
            background = Color(0xFFF4F5F8), onBackground = Color(0xFF11141B),
            surface = Color(0xFFF4F5F8), onSurface = Color(0xFF11141B),
            surfaceVariant = Color(0xFFF0F2F6), onSurfaceVariant = Color(0xFF596275),
            surfaceContainerLowest = Color.White, surfaceContainerLow = Color(0xFFFAFAFC),
            surfaceContainer = Color.White, surfaceContainerHigh = Color(0xFFF0F2F6),
            surfaceContainerHighest = Color(0xFFE7EAF0),
            outline = Color(0xFFC9CED8), outlineVariant = Color(0xFFE1E4EA),
            error = Color(0xFFB42318), onError = Color.White,
            errorContainer = Color(0xFFFCE4E2), onErrorContainer = Color(0xFF8C1D18),
        )
    }

    val extended = (if (dark) DarkExtended else LightExtended).copy(
        accentText = if (dark) accent.textOnDark else accent.textOnLight,
    )

    CompositionLocalProvider(
        LocalExtendedColors provides extended,
        LocalIsDark provides dark,
        LocalAccent provides accent,
    ) {
        MaterialTheme(
            colorScheme = scheme,
            typography = AppTypography,
            shapes = Shapes(
                extraSmall = RoundedCornerShape(8.dp),
                small = RoundedCornerShape(12.dp),
                medium = RoundedCornerShape(16.dp),
                large = RoundedCornerShape(20.dp),
                extraLarge = RoundedCornerShape(28.dp),
            ),
            content = content,
        )
    }
}
