package com.movingsurface.showcontrol.ui.theme

import androidx.compose.runtime.Immutable
import androidx.compose.ui.graphics.Color

/** Brand accent: drives primary buttons, the logo tile and highlights. Same set as the hub's branding page. */
@Immutable
data class Accent(
    val key: String,
    val label: String,
    val base: Color,
    val onBase: Color,
    /** Darker shade for accent-coloured text on light backgrounds (contrast). */
    val textOnLight: Color,
    /** Lighter shade for accent-coloured text on dark backgrounds (contrast). */
    val textOnDark: Color,
)

/** Indigo is the default: it matches the app icon. */
val Accents = listOf(
    Accent("indigo", "Indigo", Color(0xFF5B5BF0), Color.White, Color(0xFF4338CA), Color(0xFFA5B4FC)),
    Accent("teal", "Teal", Color(0xFF14B8A6), Color(0xFF04201A), Color(0xFF0F766E), Color(0xFF5EEAD4)),
    Accent("amber", "Amber", Color(0xFFF5A524), Color(0xFF1F1300), Color(0xFF92400E), Color(0xFFFCC860)),
    Accent("violet", "Violet", Color(0xFF8B5CF6), Color.White, Color(0xFF6D28D9), Color(0xFFC4B5FD)),
    Accent("blue", "Blue", Color(0xFF3B82F6), Color.White, Color(0xFF1D4ED8), Color(0xFF93C5FD)),
    Accent("coral", "Coral", Color(0xFFF97350), Color(0xFF2A0D03), Color(0xFFC2410C), Color(0xFFFDA48A)),
)

fun accentFor(key: String?): Accent = Accents.firstOrNull { it.key == key } ?: Accents.first()

/** Colour labels for scene and app tiles, with a version for each theme. */
@Immutable
data class LabelColors(val fg: Color, val bg: Color, val border: Color, val sub: Color)

private val darkLabels = mapOf(
    "teal" to LabelColors(Color(0xFF5BE3C4), Color(0xFF10302C), Color(0xFF1C4A43), Color(0xFFA7D9CC)),
    "amber" to LabelColors(Color(0xFFFFB020), Color(0xFF33260A), Color(0xFF5A4313), Color(0xFFF2D59A)),
    "violet" to LabelColors(Color(0xFFB3A8FF), Color(0xFF231F3D), Color(0xFF383160), Color(0xFFC8C1FF)),
    "blue" to LabelColors(Color(0xFF7DBBFF), Color(0xFF0F2438), Color(0xFF1B3B5A), Color(0xFFA9CCF2)),
    "coral" to LabelColors(Color(0xFFFF9A6B), Color(0xFF36190F), Color(0xFF5C2B19), Color(0xFFFFC3A6)),
    "red" to LabelColors(Color(0xFFFF8A8A), Color(0xFF2A1416), Color(0xFF5A2226), Color(0xFFFFB4B4)),
    "gray" to LabelColors(Color(0xFFC9CDD4), Color(0xFF1F2228), Color(0xFF2E323A), Color(0xFFA3A8B3)),
)

private val lightLabels = mapOf(
    "teal" to LabelColors(Color(0xFF0E7A63), Color(0xFFDDF5EE), Color(0xFFB7E8DA), Color(0xFF2F6F60)),
    "amber" to LabelColors(Color(0xFF8A5A00), Color(0xFFFFF1D6), Color(0xFFF5DCA8), Color(0xFF7A5A1E)),
    "violet" to LabelColors(Color(0xFF5B4BD1), Color(0xFFECE9FF), Color(0xFFD6D0FF), Color(0xFF4D4490)),
    "blue" to LabelColors(Color(0xFF1F6FC2), Color(0xFFE3F0FF), Color(0xFFC3DDFB), Color(0xFF2F5B88)),
    "coral" to LabelColors(Color(0xFFB6461A), Color(0xFFFFE9DF), Color(0xFFF8CDB9), Color(0xFF8A4A2E)),
    "red" to LabelColors(Color(0xFFB3261E), Color(0xFFFCE4E2), Color(0xFFF4C3BF), Color(0xFF8C3B36)),
    "gray" to LabelColors(Color(0xFF3F4550), Color(0xFFECEEF1), Color(0xFFD9DCE1), Color(0xFF5C6370)),
)

val LabelKeys = listOf("teal", "amber", "violet", "blue", "coral", "red", "gray")

fun labelColors(key: String?, dark: Boolean): LabelColors {
    val map = if (dark) darkLabels else lightLabels
    return map[key] ?: map.getValue("gray")
}
