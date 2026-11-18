package com.movingsurface.showcontrol.ui.components

import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.produceState
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.ImageBitmap
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import com.movingsurface.showcontrol.data.BrandLogo
import com.movingsurface.showcontrol.ui.theme.ShowTheme

/** The brand logo if one is set, otherwise the brand's initials on the accent colour. */
@Composable
fun BrandMark(brandName: String, logoVersion: Long, size: Dp = 44.dp) {
    val context = LocalContext.current
    val logo: ImageBitmap? by produceState<ImageBitmap?>(null, logoVersion) {
        value = if (logoVersion == 0L) null else BrandLogo.load(context)?.asImageBitmap()
    }
    val shape = RoundedCornerShape(size * 0.27f)
    val bmp = logo
    if (bmp != null) {
        Box(Modifier.size(size).clip(shape).background(ShowTheme.colors.card)) {
            Image(
                bitmap = bmp,
                contentDescription = "$brandName logo",
                contentScale = ContentScale.Fit,
                modifier = Modifier.fillMaxSize().padding(size * 0.06f),
            )
        }
    } else {
        Box(Modifier.size(size).clip(shape).background(MaterialTheme.colorScheme.primary), contentAlignment = Alignment.Center) {
            Text(
                brandInitials(brandName),
                color = MaterialTheme.colorScheme.onPrimary,
                style = MaterialTheme.typography.titleSmall,
                fontWeight = FontWeight.Bold,
            )
        }
    }
}

fun brandInitials(name: String): String {
    val parts = name.trim().split(Regex("\\s+")).filter { it.isNotEmpty() }
    return when {
        parts.isEmpty() -> "SC"
        parts.size == 1 -> parts[0].take(2).uppercase()
        else -> (parts[0].take(1) + parts[1].take(1)).uppercase()
    }
}
