package com.movingsurface.showcontrol.ui.theme

import androidx.compose.material3.Typography
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.Font
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.sp
import com.movingsurface.showcontrol.R

/**
 * Inter (SIL Open Font License, bundled in res/font) in four weights: Regular 400, Medium 500,
 * SemiBold 600 and Bold 700. The same face and weights as the PC dashboard and the hub.
 * Technical values (addresses, ports, PINs) use [Mono].
 */
val Inter: FontFamily = FontFamily(
    Font(R.font.inter_regular, FontWeight.Normal),
    Font(R.font.inter_medium, FontWeight.Medium),
    Font(R.font.inter_semibold, FontWeight.SemiBold),
    Font(R.font.inter_bold, FontWeight.Bold),
)
val Mono: FontFamily = FontFamily.Monospace

private fun style(size: Int, line: Int, weight: FontWeight = FontWeight.Normal, tracking: Double = 0.0) =
    TextStyle(fontFamily = Inter, fontWeight = weight, fontSize = size.sp, lineHeight = line.sp, letterSpacing = tracking.sp)

/** Material 3 type scale, tuned for Inter (slightly tighter tracking on large sizes). */
val AppTypography = Typography(
    displayLarge = style(48, 56, FontWeight.Bold, -1.0),
    displayMedium = style(40, 48, FontWeight.Bold, -0.8),
    displaySmall = style(34, 42, FontWeight.Bold, -0.6),
    headlineLarge = style(30, 38, FontWeight.Bold, -0.5),
    headlineMedium = style(26, 32, FontWeight.Bold, -0.4),
    headlineSmall = style(22, 28, FontWeight.SemiBold, -0.3),
    titleLarge = style(20, 26, FontWeight.SemiBold, -0.2),
    titleMedium = style(17, 22, FontWeight.SemiBold, -0.1),
    titleSmall = style(15, 20, FontWeight.SemiBold),
    bodyLarge = style(16, 24),
    bodyMedium = style(14, 20),
    bodySmall = style(13, 18),
    labelLarge = style(15, 20, FontWeight.SemiBold),
    labelMedium = style(12, 16, FontWeight.Medium, 0.1),
    labelSmall = style(11, 14, FontWeight.Medium, 0.2),
)
