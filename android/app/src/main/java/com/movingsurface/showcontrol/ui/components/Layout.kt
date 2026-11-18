package com.movingsurface.showcontrol.ui.components

import androidx.compose.runtime.Composable
import androidx.compose.ui.platform.LocalConfiguration

/**
 * How many grid columns fit the screen for cells at least [minCellDp] wide: phones get one or two,
 * tablets and landscape more (up to [max]). Used for room cards and tiles.
 */
@Composable
fun gridColumns(minCellDp: Int, max: Int, horizontalPaddingDp: Int = 32): Int {
    val width = LocalConfiguration.current.screenWidthDp - horizontalPaddingDp
    return (width / minCellDp).coerceIn(1, max)
}
