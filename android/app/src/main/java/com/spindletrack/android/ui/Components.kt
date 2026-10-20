package com.spindletrack.android.ui

import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.interaction.collectIsPressedAsState
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyListScope
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.KeyboardArrowRight
import androidx.compose.material.icons.filled.Check
import androidx.compose.material.icons.filled.Info
import androidx.compose.material.icons.filled.Lock
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material.icons.filled.Stop
import androidx.compose.material.icons.filled.Warning
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.draw.clip
import androidx.compose.ui.geometry.CornerRadius
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.Shape
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.AnnotatedString
import androidx.compose.ui.text.SpanStyle
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.buildAnnotatedString
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.text.withStyle
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.spindletrack.android.R
import com.spindletrack.android.data.*
import com.spindletrack.android.ui.theme.Spindle

// The pieces every screen is built from, matching the iPhone app's: cards on a grey page, a marigold main action,
// project dots and split bars in the projects' colours.

// MARK: - Brand

/** The Spindle logo: the mark and the name. */
@Composable
fun Logo(modifier: Modifier = Modifier, height: Dp = 32.dp) {
    Image(R.drawable.spindle_logo, "Spindle", modifier.height(height))
}

@Composable
private fun Image(id: Int, description: String?, modifier: Modifier) {
    androidx.compose.foundation.Image(painterResource(id), description, modifier)
}

/** A person's initials in a circle, e.g. "MN" for Mara Novak. */
@Composable
fun Avatar(name: String, size: Dp = 48.dp) {
    val words = name.split(Regex("\\s+")).filter { it.isNotBlank() }
    val letters = listOfNotNull(words.firstOrNull()?.first(), words.drop(1).lastOrNull()?.first()).joinToString("").uppercase().ifEmpty { "?" }
    Box(Modifier.size(size).clip(CircleShape).background(Spindle.colors.chip), contentAlignment = Alignment.Center) {
        Text(letters, style = TextStyle(fontSize = (size.value * 0.38f).sp, fontWeight = FontWeight.SemiBold), color = MaterialTheme.colorScheme.onSurfaceVariant)
    }
}

// MARK: - Buttons

/** The main action of a screen, full width in marigold with ink on it, in both modes. */
@Composable
fun ProminentButton(text: String, onClick: () -> Unit, modifier: Modifier = Modifier, enabled: Boolean = true, busy: Boolean = false) {
    Button(
        onClick = onClick, enabled = enabled && !busy, modifier = modifier.fillMaxWidth().heightIn(min = 52.dp),
        shape = CircleShape,
        colors = ButtonDefaults.buttonColors(
            containerColor = Spindle.colors.brand, contentColor = Spindle.colors.ink,
            disabledContainerColor = Spindle.colors.brand.copy(alpha = 0.5f), disabledContentColor = Spindle.colors.ink.copy(alpha = 0.6f),
        ),
    ) {
        if (busy) CircularProgressIndicator(Modifier.size(18.dp).padding(end = 0.dp), strokeWidth = 2.dp, color = Spindle.colors.ink)
        if (busy) Spacer(Modifier.width(10.dp))
        Text(text, style = MaterialTheme.typography.titleMedium)
    }
}

/** Another way to do a screen's main action: a quiet surface, so the marigold stays on the main one. */
@Composable
fun SecondaryButton(
    text: String, onClick: () -> Unit, modifier: Modifier = Modifier, enabled: Boolean = true, icon: ImageVector? = null,
    /** False for a mark in its own colours, such as Google's. */
    tintIcon: Boolean = true,
) {
    OutlinedButton(
        onClick = onClick, enabled = enabled, modifier = modifier.fillMaxWidth().heightIn(min = 52.dp), shape = CircleShape,
        colors = ButtonDefaults.outlinedButtonColors(containerColor = MaterialTheme.colorScheme.surfaceContainerLow, contentColor = MaterialTheme.colorScheme.onSurface),
        border = androidx.compose.foundation.BorderStroke(1.dp, Spindle.colors.hairline),
    ) {
        if (icon != null) { Icon(icon, null, Modifier.size(20.dp), tint = if (tintIcon) LocalContentColor.current else Color.Unspecified); Spacer(Modifier.width(8.dp)) }
        Text(text, style = MaterialTheme.typography.titleMedium.copy(fontWeight = FontWeight.Medium))
    }
}

/** A link in the brand colour. */
@Composable
fun BrandLink(text: String, onClick: () -> Unit, modifier: Modifier = Modifier, style: TextStyle = MaterialTheme.typography.bodyLarge, enabled: Boolean = true) {
    Text(
        text, style = style, color = if (enabled) Spindle.colors.brandText else MaterialTheme.colorScheme.onSurfaceVariant,
        modifier = modifier.clip(RoundedCornerShape(6.dp)).clickable(enabled = enabled, role = Role.Button, onClick = onClick).padding(vertical = 6.dp, horizontal = 2.dp),
    )
}

/** The round play button, in marigold with ink on it. */
@Composable
fun StartButton(onClick: () -> Unit, size: Dp = 52.dp, enabled: Boolean = true) {
    RoundButton(onClick, Spindle.colors.brand, enabled, size, "Start timer") {
        Icon(Icons.Filled.PlayArrow, null, Modifier.size(size * 0.52f), tint = Spindle.colors.ink)
    }
}

/** The round stop button, in red. */
@Composable
fun StopButton(onClick: () -> Unit, size: Dp = 52.dp, enabled: Boolean = true) {
    RoundButton(onClick, Spindle.colors.danger, enabled, size, "Stop timer") {
        Icon(Icons.Filled.Stop, null, Modifier.size(size * 0.48f), tint = Color.White)
    }
}

@Composable
private fun RoundButton(onClick: () -> Unit, color: Color, enabled: Boolean, size: Dp, label: String, content: @Composable () -> Unit) {
    val interaction = remember { MutableInteractionSource() }
    val pressed by interaction.collectIsPressedAsState()
    val alpha by animateFloatAsState(if (!enabled) 0.5f else if (pressed) 0.8f else 1f, label = "press")
    Box(
        Modifier.size(size).clip(CircleShape).background(color.copy(alpha = color.alpha * alpha))
            .clickable(interaction, ripple(), enabled = enabled, role = Role.Button, onClick = onClick)
            .semantics { contentDescription = label },
        contentAlignment = Alignment.Center,
    ) { content() }
}

/** A field's value in a small capsule, tapped to change it. */
@Composable
fun ChipButton(onClick: () -> Unit, modifier: Modifier = Modifier, label: String? = null, content: @Composable RowScope.() -> Unit) {
    Row(
        modifier.heightIn(min = 34.dp).widthIn(min = 38.dp).clip(CircleShape).background(Spindle.colors.chip)
            .clickable(role = Role.Button, onClick = onClick).then(if (label != null) Modifier.semantics { contentDescription = label } else Modifier)
            .padding(horizontal = 11.dp),
        verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.Center,
    ) {
        CompositionLocalProvider(LocalTextStyle provides MaterialTheme.typography.bodyMedium) { content() }
    }
}

// MARK: - Projects, tags and splits

@Composable
fun ProjectDot(color: String?, size: Dp = 9.dp) {
    Box(Modifier.size(size).clip(CircleShape).background(projectColor(color)))
}

/** "● Website · Design · Acme": the project with its task and client. */
@Composable
fun ProjectLabel(project: ProjectResponse, task: TaskResponse? = null, client: ClientResponse? = null, style: TextStyle = MaterialTheme.typography.bodyMedium, modifier: Modifier = Modifier) {
    Row(modifier, verticalAlignment = Alignment.CenterVertically) {
        ProjectDot(project.color)
        Spacer(Modifier.width(6.dp))
        Text(
            buildAnnotatedString {
                withStyle(SpanStyle(color = readableColor(project.color, Spindle.colors.dark) ?: Color.Unspecified, fontWeight = FontWeight.Medium)) { append(project.name) }
                val muted = SpanStyle(color = MaterialTheme.colorScheme.onSurfaceVariant)
                task?.let { withStyle(muted) { append(" · ${it.name}") } }
                client?.let { withStyle(muted) { append(" · ${it.name}") } }
            },
            style = style, maxLines = 1, overflow = TextOverflow.Ellipsis,
        )
    }
}

/** A tag's name in a small capsule. */
@Composable
fun TagChip(name: String) {
    Text(
        name, style = MaterialTheme.typography.labelMedium, maxLines = 1, color = MaterialTheme.colorScheme.onSurfaceVariant,
        modifier = Modifier.clip(CircleShape).background(Spindle.colors.chip).padding(horizontal = 7.dp, vertical = 2.dp),
    )
}

/** Overlapping dots, one for each part's project. */
@Composable
fun SplitDots(state: AppState, parts: List<TimeShareResponse>, size: Dp = 9.dp) {
    val ring = MaterialTheme.colorScheme.surfaceContainerLow
    Row(horizontalArrangement = Arrangement.spacedBy(-(size * 0.35f))) {
        parts.forEach { part ->
            Box(Modifier.size(size).clip(CircleShape).background(ring).padding(1.dp).clip(CircleShape).background(state.colorOf(part) ?: MaterialTheme.colorScheme.onSurfaceVariant))
        }
    }
}

/** "●● Website 60% · Research 40%": where a split entry's time went. */
@Composable
fun SplitLabel(state: AppState, parts: List<TimeShareResponse>, style: TextStyle = MaterialTheme.typography.bodyMedium, modifier: Modifier = Modifier) {
    Row(modifier.semantics(mergeDescendants = true) { contentDescription = "Split: ${state.describe(parts)}" }, verticalAlignment = Alignment.CenterVertically) {
        SplitDots(state, parts)
        Spacer(Modifier.width(6.dp))
        Text(splitText(state, parts, Spindle.colors.dark, MaterialTheme.colorScheme.onSurfaceVariant), style = style, maxLines = 1, overflow = TextOverflow.Ellipsis)
    }
}

/** The projects of a split, each in its colour with its share. */
fun splitText(state: AppState, parts: List<TimeShareResponse>, dark: Boolean, muted: Color): AnnotatedString = buildAnnotatedString {
    parts.forEachIndexed { index, part ->
        if (index > 0) withStyle(SpanStyle(color = muted)) { append(" · ") }
        val project = state.project(part.projectId)
        withStyle(SpanStyle(color = readableColor(project?.color, dark) ?: muted, fontWeight = FontWeight.Medium)) { append(project?.name ?: "No project") }
        withStyle(SpanStyle(color = muted)) { append(" ${part.percent}%") }
    }
}

/** Where a split entry's time went, as a thread: a segment per project, as long as its share. */
@Composable
fun SplitThread(state: AppState, parts: List<TimeShareResponse>, modifier: Modifier = Modifier, height: Dp = 3.dp) {
    val fallback = MaterialTheme.colorScheme.onSurfaceVariant
    val colors = parts.map { state.colorOf(it) ?: fallback }
    Canvas(modifier.fillMaxWidth().height(height)) {
        val gap = 2.dp.toPx()
        val room = size.width - gap * (parts.size - 1)
        var x = 0f
        parts.forEachIndexed { index, part ->
            val width = max(room * part.percent / 100f, 3.dp.toPx())
            drawRoundRect(colors[index], Offset(x, 0f), Size(width, size.height), CornerRadius(size.height / 2))
            x += width + gap
        }
    }
}

private fun max(a: Float, b: Float) = if (a > b) a else b

/** The time tracked against an estimate: "5h of 10h", with a bar that turns red past it. */
@Composable
fun EstimateProgress(tracked: Long, estimate: Long?) {
    if (estimate == null || estimate <= 0) {
        Text("—", color = MaterialTheme.colorScheme.onSurfaceVariant)
        return
    }
    val share = tracked.toFloat() / estimate
    val over = share > 1
    Row(verticalAlignment = Alignment.CenterVertically) {
        LinearProgressIndicator(
            progress = { share.coerceAtMost(1f) }, modifier = Modifier.width(72.dp).height(4.dp).clip(CircleShape),
            color = if (over) MaterialTheme.colorScheme.error else Spindle.colors.brand, trackColor = Spindle.colors.chip,
            gapSize = 0.dp, drawStopIndicator = {},
        )
        Spacer(Modifier.width(8.dp))
        Text(
            "${Durations.hoursMinutes(tracked)} of ${Durations.hoursMinutes(estimate)}", style = MaterialTheme.typography.labelMedium,
            color = if (over) MaterialTheme.colorScheme.error else MaterialTheme.colorScheme.onSurfaceVariant, maxLines = 1,
        )
    }
}

// MARK: - Grouped lists

/** A card's corners for a row at `index` of `count`: rounded on the outside of the card only. */
fun rowShape(index: Int, count: Int, radius: Dp = 16.dp): Shape = RoundedCornerShape(
    topStart = if (index == 0) radius else 0.dp, topEnd = if (index == 0) radius else 0.dp,
    bottomStart = if (index == count - 1) radius else 0.dp, bottomEnd = if (index == count - 1) radius else 0.dp,
)

/** A row of a card in a grouped list: the card's surface, with a hairline above every row but the first. */
@Composable
fun CardRow(index: Int, count: Int, modifier: Modifier = Modifier, onClick: (() -> Unit)? = null, onLongClick: (() -> Unit)? = null, inset: Dp = 16.dp, content: @Composable () -> Unit) {
    val shape = rowShape(index, count)
    Box(
        modifier.padding(horizontal = 16.dp).fillMaxWidth().clip(shape).background(MaterialTheme.colorScheme.surfaceContainerLow)
            .then(
                if (onClick != null || onLongClick != null) Modifier.combinedClickableCompat(onClick ?: {}, onLongClick) else Modifier
            ),
    ) {
        content()
        if (index > 0) HorizontalDivider(Modifier.padding(start = inset).align(Alignment.TopStart), thickness = 0.5.dp, color = Spindle.colors.hairline)
    }
}

/** A whole card of rows, for content that isn't in a lazy list. */
@Composable
fun Card(modifier: Modifier = Modifier, content: @Composable ColumnScope.() -> Unit) {
    Column(modifier.padding(horizontal = 16.dp).fillMaxWidth().clip(RoundedCornerShape(16.dp)).background(MaterialTheme.colorScheme.surfaceContainerLow), content = content)
}

/** A hairline between rows of a card. */
@Composable
fun RowDivider(inset: Dp = 16.dp) {
    HorizontalDivider(Modifier.padding(start = inset), thickness = 0.5.dp, color = Spindle.colors.hairline)
}

/** The heading above a card, with something on its right such as a total or a menu. */
@Composable
fun SectionHeader(text: String, modifier: Modifier = Modifier, trailing: @Composable (RowScope.() -> Unit)? = null) {
    Row(
        modifier.fillMaxWidth().padding(start = 32.dp, end = 32.dp, top = 22.dp, bottom = 7.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Text(text, style = MaterialTheme.typography.titleSmall.copy(fontWeight = FontWeight.Medium), color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.weight(1f))
        if (trailing != null) CompositionLocalProvider(LocalContentColor provides MaterialTheme.colorScheme.onSurfaceVariant, LocalTextStyle provides MaterialTheme.typography.titleSmall.copy(fontWeight = FontWeight.Medium)) { trailing() }
    }
}

/** What a card is for, under it. */
@Composable
fun SectionFooter(text: String, modifier: Modifier = Modifier, color: Color = MaterialTheme.colorScheme.onSurfaceVariant) {
    Text(text, style = MaterialTheme.typography.bodySmall, color = color, modifier = modifier.fillMaxWidth().padding(start = 32.dp, end = 32.dp, top = 7.dp))
}

/** A label with its value on the right, optionally leading somewhere. */
@Composable
fun ValueRow(label: String, value: String? = null, onClick: (() -> Unit)? = null, chevron: Boolean = onClick != null, labelColor: Color = Color.Unspecified, valueColor: Color = MaterialTheme.colorScheme.onSurfaceVariant, trailing: (@Composable () -> Unit)? = null) {
    Row(
        Modifier.fillMaxWidth().then(if (onClick != null) Modifier.clickable(onClick = onClick) else Modifier).heightIn(min = 52.dp).padding(horizontal = 16.dp, vertical = 10.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Text(label, style = MaterialTheme.typography.bodyLarge, color = labelColor, modifier = Modifier.padding(end = 12.dp))
        Spacer(Modifier.weight(1f))
        if (trailing != null) trailing()
        else if (value != null) Text(value, style = MaterialTheme.typography.bodyLarge, color = valueColor, maxLines = 1, overflow = TextOverflow.Ellipsis)
        if (chevron) Icon(Icons.AutoMirrored.Filled.KeyboardArrowRight, null, Modifier.padding(start = 4.dp).size(20.dp), tint = MaterialTheme.colorScheme.outline)
    }
}

/** A label with a switch. */
@Composable
fun SwitchRow(label: String, checked: Boolean, onChange: (Boolean) -> Unit, enabled: Boolean = true) {
    Row(
        Modifier.fillMaxWidth().clickable(enabled = enabled, role = Role.Switch) { onChange(!checked) }.heightIn(min = 52.dp).padding(horizontal = 16.dp, vertical = 6.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Text(label, style = MaterialTheme.typography.bodyLarge, modifier = Modifier.weight(1f).padding(end = 12.dp))
        Switch(checked, onChange, enabled = enabled, colors = SwitchDefaults.colors(checkedTrackColor = MaterialTheme.colorScheme.primary, checkedThumbColor = MaterialTheme.colorScheme.onPrimary))
    }
}

/** A row that acts, such as "Sign out", in the brand colour or red. */
@Composable
fun ActionRow(text: String, onClick: () -> Unit, destructive: Boolean = false, enabled: Boolean = true, trailing: (@Composable () -> Unit)? = null) {
    Row(
        Modifier.fillMaxWidth().clickable(enabled = enabled, role = Role.Button, onClick = onClick).heightIn(min = 52.dp).padding(horizontal = 16.dp, vertical = 10.dp).alpha(if (enabled) 1f else 0.5f),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Text(text, style = MaterialTheme.typography.bodyLarge, color = if (destructive) MaterialTheme.colorScheme.error else Spindle.colors.brandText, modifier = Modifier.weight(1f))
        trailing?.invoke()
    }
}

/** A choice from a short list, shown as its value in the brand colour with the options in a menu. */
@Composable
fun <T> MenuRow(label: String, value: T, options: List<Pair<T, String>>, onSelect: (T) -> Unit, enabled: Boolean = true) {
    var open by remember { mutableStateOf(false) }
    Box {
        ValueRow(label, options.firstOrNull { it.first == value }?.second ?: "", onClick = if (enabled) ({ open = true }) else null, chevron = false, valueColor = if (enabled) Spindle.colors.brandText else MaterialTheme.colorScheme.onSurfaceVariant)
        DropdownMenu(open, { open = false }, Modifier.align(Alignment.TopEnd), offset = androidx.compose.ui.unit.DpOffset((-16).dp, 0.dp)) {
            options.forEach { (key, title) ->
                DropdownMenuItem(
                    text = { Text(title) }, onClick = { open = false; onSelect(key) },
                    trailingIcon = if (key == value) ({ Icon(Icons.Filled.Check, null, tint = Spindle.colors.brandText) }) else null,
                )
            }
        }
    }
}

/** Items in a card, one row each, for a lazy list. */
fun <T> LazyListScope.cardItems(items: List<T>, key: ((T) -> Any)? = null, content: @Composable (index: Int, item: T) -> Unit) {
    items(items.size, key = key?.let { k -> { index: Int -> k(items[index]) } }) { index -> content(index, items[index]) }
}

// MARK: - Messages

/** A message across the top of a form: what went wrong, or a notice. */
@Composable
fun FormBanner(text: String, error: Boolean = true, modifier: Modifier = Modifier) {
    val color = if (error) MaterialTheme.colorScheme.error else MaterialTheme.colorScheme.onSurfaceVariant
    Row(
        modifier.fillMaxWidth().clip(RoundedCornerShape(12.dp))
            .background(if (error) MaterialTheme.colorScheme.error.copy(alpha = 0.1f) else Spindle.colors.chip).padding(12.dp),
        verticalAlignment = Alignment.Top,
    ) {
        Icon(if (error) Icons.Filled.Warning else Icons.Filled.Info, null, Modifier.size(18.dp).padding(top = 1.dp), tint = color)
        Spacer(Modifier.width(10.dp))
        Text(text, style = MaterialTheme.typography.bodyMedium)
    }
}

/**
 * Says why nothing can be changed, for as long as the current workspace's organization is read-only. Like the
 * iPhone's, it doesn't point to choosing a plan: store apps may not lead to buying outside the store.
 */
@Composable
fun ReadOnlyNotice(organization: OrganizationSummary, modifier: Modifier = Modifier) {
    val reason = if (organization.role == "member") "Its trial or plan has ended, so time can't be added or changed. Everything is still here. Its owner can bring it back."
        else "Its trial or plan has ended. Everything is still here, and you can read, report and export it, but not add or change time."
    Row(modifier.fillMaxWidth().semantics(mergeDescendants = true) {}, verticalAlignment = Alignment.Top) {
        Icon(Icons.Filled.Lock, null, Modifier.size(18.dp).padding(top = 1.dp), tint = MaterialTheme.colorScheme.error)
        Spacer(Modifier.width(10.dp))
        Text(buildAnnotatedString {
            withStyle(SpanStyle(fontWeight = FontWeight.SemiBold)) { append("${organization.name} is read-only.") }
            append(" $reason")
        }, style = MaterialTheme.typography.bodyMedium)
    }
}

/** A screen or list with nothing in it yet: what it is and what to do. */
@Composable
fun EmptyState(icon: ImageVector, title: String, message: String? = null, modifier: Modifier = Modifier, action: (@Composable () -> Unit)? = null) {
    Column(modifier.fillMaxWidth().padding(horizontal = 32.dp, vertical = 40.dp), horizontalAlignment = Alignment.CenterHorizontally) {
        Icon(icon, null, Modifier.size(40.dp), tint = MaterialTheme.colorScheme.onSurfaceVariant)
        Spacer(Modifier.height(12.dp))
        Text(title, style = MaterialTheme.typography.titleMedium, textAlign = androidx.compose.ui.text.style.TextAlign.Center)
        if (message != null) {
            Spacer(Modifier.height(6.dp))
            Text(message, style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant, textAlign = androidx.compose.ui.text.style.TextAlign.Center)
        }
        if (action != null) { Spacer(Modifier.height(16.dp)); action() }
    }
}

/** A text field with its label above and its error below, on a rounded card, as on the sign-in screens. */
@Composable
fun LabeledField(label: String, error: String? = null, hint: String? = null, content: @Composable () -> Unit) {
    Column(verticalArrangement = Arrangement.spacedBy(6.dp)) {
        Text(label, style = MaterialTheme.typography.titleSmall.copy(fontWeight = FontWeight.Medium), color = MaterialTheme.colorScheme.onSurfaceVariant)
        Box(
            Modifier.fillMaxWidth().heightIn(min = 52.dp).clip(RoundedCornerShape(12.dp)).background(MaterialTheme.colorScheme.surfaceContainerLow)
                .then(if (error != null) Modifier.border(1.dp, MaterialTheme.colorScheme.error.copy(alpha = 0.7f), RoundedCornerShape(12.dp)) else Modifier)
                .padding(horizontal = 14.dp),
            contentAlignment = Alignment.CenterStart,
        ) { content() }
        when {
            error != null -> Text(error, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.error)
            hint != null -> Text(hint, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
        }
    }
}
