package com.movingsurface.showcontrol.ui.components

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.rounded.Menu
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.Text
import androidx.compose.material3.rememberModalBottomSheetState
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import com.movingsurface.showcontrol.ui.theme.ShowTheme

/**
 * Advanced operations (power, Wake-on-LAN, stop all, share, remove…) live behind the hamburger menu so the
 * screens stay focused on running the show. The same list is used in the main screen's side drawer and in
 * the room and PC screens' menu sheet.
 */
data class OpItem(
    val label: String,
    val icon: ImageVector,
    val supporting: String? = null,
    val enabled: Boolean = true,
    val danger: Boolean = false,
    val onClick: () -> Unit,
)

data class OpSection(val title: String, val items: List<OpItem>)

/** The hamburger button, for a top bar or a header. */
@Composable
fun MenuButton(onClick: () -> Unit, description: String = "Menu") {
    IconButton(onClick = onClick) { Icon(Icons.Rounded.Menu, contentDescription = description) }
}

/** Sections of operations. [onPicked] runs before an item's action (e.g. to close the menu). */
@Composable
fun OperationsList(sections: List<OpSection>, onPicked: () -> Unit = {}, modifier: Modifier = Modifier) {
    Column(modifier) {
        sections.filter { it.items.isNotEmpty() }.forEachIndexed { index, section ->
            if (index > 0) HorizontalDivider(Modifier.padding(vertical = 8.dp), color = MaterialTheme.colorScheme.outlineVariant)
            Text(
                section.title.uppercase(),
                style = MaterialTheme.typography.labelSmall,
                fontWeight = FontWeight.SemiBold,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                modifier = Modifier.padding(start = 16.dp, top = 8.dp, bottom = 4.dp),
            )
            section.items.forEach { item -> OperationRow(item) { onPicked(); item.onClick() } }
        }
    }
}

@Composable
private fun OperationRow(item: OpItem, onClick: () -> Unit) {
    val c = ShowTheme.colors
    val tint = if (item.danger) c.danger else MaterialTheme.colorScheme.onSurface
    Row(
        verticalAlignment = Alignment.CenterVertically,
        modifier = Modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(12.dp))
            .clickable(enabled = item.enabled, onClick = onClick)
            .alpha(if (item.enabled) 1f else 0.42f)
            .padding(horizontal = 16.dp, vertical = 12.dp),
    ) {
        Icon(item.icon, contentDescription = null, tint = tint, modifier = Modifier.size(22.dp))
        Spacer(Modifier.width(16.dp))
        Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(2.dp)) {
            Text(item.label, style = MaterialTheme.typography.bodyLarge, fontWeight = FontWeight.Medium, color = tint)
            item.supporting?.let { Text(it, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant) }
        }
    }
}

/** The room and PC screens' menu: a sheet from the bottom with a title and the operations. */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun OperationsSheet(title: String, subtitle: String?, sections: List<OpSection>, onDismiss: () -> Unit) {
    ModalBottomSheet(
        onDismissRequest = onDismiss,
        sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true),
        containerColor = MaterialTheme.colorScheme.surfaceContainer,
    ) {
        Column(Modifier.verticalScroll(rememberScrollState()).padding(horizontal = 8.dp).padding(bottom = 16.dp).navigationBarsPadding()) {
            Column(Modifier.padding(horizontal = 16.dp, vertical = 4.dp)) {
                Text(title, style = MaterialTheme.typography.titleLarge)
                subtitle?.let { Text(it, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant) }
            }
            OperationsList(sections, onPicked = onDismiss, modifier = Modifier.padding(top = 8.dp))
        }
    }
}
