package com.spindletrack.android.ui

import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.gestures.detectHorizontalDragGestures
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.KeyboardArrowRight
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.AttachMoney
import androidx.compose.material.icons.filled.Remove
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.scale
import androidx.compose.ui.hapticfeedback.HapticFeedbackType
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.layout.onSizeChanged
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.platform.LocalHapticFeedback
import androidx.compose.ui.semantics.*
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.KeyboardCapitalization
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import com.spindletrack.android.data.*
import com.spindletrack.android.ui.theme.Spindle
import kotlin.math.abs
import kotlin.math.roundToInt

/** The arithmetic of splitting an entry's time, as the server, the web and the Apple apps do it, so all of them agree. */
object SplitMath {
    /** The most parts the server allows. */
    const val MAX_PARTS = 6

    /** The smallest part, in percent. */
    const val MIN_PERCENT = 1

    /**
     * Whole numbers in proportion to [weights] that add up to [total], each at least [minimum]: what rounding leaves
     * over goes to the largest remainders, the earlier one on a tie.
     */
    fun apportion(weights: List<Int>, total: Int, minimum: Int = 0): List<Int> {
        if (weights.isEmpty()) return emptyList()
        val free = total - minimum * weights.size
        val positive = weights.map { maxOf(0, it) }
        val sum = positive.sum()
        val shares = if (sum > 0) positive else positive.map { 1 }
        val divisor = if (sum > 0) sum else weights.size
        val result = shares.map { it * free / divisor }.toMutableList()
        val rests = shares.map { it * free % divisor }
        var left = free - result.sum()
        for (index in rests.indices.sortedWith(compareByDescending<Int> { rests[it] }.thenBy { it })) {
            if (left <= 0) break
            result[index] += 1
            left -= 1
        }
        return result.map { it + minimum }
    }

    /** Divides seconds (or minutes) between parts, as reports do. */
    fun allocate(amount: Long, percents: List<Int>): List<Long> = apportion(percents, maxOf(0, amount).toInt()).map { it.toLong() }

    /** [count] equal parts; the first ones get what does not divide evenly. */
    fun equal(count: Int): List<Int> = apportion(List(count) { 1 }, 100, MIN_PERCENT)

    /**
     * Moves the boundary after part [index] to [position] percent along the bar. Only the two parts beside it change,
     * neither below the minimum. With [step], the boundary snaps to multiples of it.
     */
    fun moveBoundary(percents: List<Int>, index: Int, position: Double, step: Int = 1): List<Int> {
        val before = percents.take(index).sum()
        val pair = percents[index] + percents[index + 1]
        val snapped = (position / step).roundToInt() * step
        val at = minOf(before + pair - MIN_PERCENT, maxOf(before + MIN_PERCENT, snapped))
        return percents.toMutableList().apply {
            this[index] = at - before
            this[index + 1] = pair - this[index]
        }
    }

    /** Gives part [index] the share asked for it; the others make room in proportion to their size. */
    fun setting(percents: List<Int>, index: Int, value: Int): List<Int> {
        val clamped = minOf(100 - (percents.size - 1) * MIN_PERCENT, maxOf(MIN_PERCENT, value))
        val others = percents.toMutableList().apply { removeAt(index) }
        return apportion(others, 100 - clamped, MIN_PERCENT).toMutableList().apply { add(index, clamped) }
    }

    /** Adds a part with an equal share; the others shrink in proportion. */
    fun adding(percents: List<Int>): List<Int> {
        val share = (100.0 / (percents.size + 1)).roundToInt()
        return apportion(percents, 100 - share, MIN_PERCENT) + share
    }

    /** Removes part [index]; its time goes to the others in proportion to their size. */
    fun removing(percents: List<Int>, index: Int): List<Int> {
        val rest = percents.toMutableList().apply { removeAt(index) }
        return apportion(rest, 100, if (rest.size > 1) MIN_PERCENT else 0)
    }
}

/** The parts with these shares, in order. */
fun List<TimeShareResponse>.withPercents(percents: List<Int>) = mapIndexed { index, part -> part.copy(percent = percents[index]) }

/**
 * The draft with its time split between [parts]; the largest is the entry's project, for apps that show one. Fewer than
 * two parts put the time on the one project left and send no shares, which ends a split on the server.
 */
fun EntryDraft.splitting(parts: List<TimeShareResponse>?): EntryDraft {
    if (parts != null && parts.size > 1) {
        // The server keeps the first of equal largest parts.
        val largest = parts.first { part -> part.percent == parts.maxOf { it.percent } }
        return copy(projectId = largest.projectId, taskId = largest.taskId, isBillable = largest.isBillable, shares = parts.map { it.toRequest() })
    }
    val only = parts?.singleOrNull()
    return copy(
        projectId = only?.projectId ?: projectId, taskId = if (only != null) only.taskId else taskId,
        isBillable = only?.isBillable ?: isBillable, shares = emptyList(),
    )
}

/**
 * What each part of a split entry is missing that the workspace requires, by the part's position. A part's own
 * description is required when the parts are described one by one.
 */
fun AppState.missingInParts(parts: List<TimeShareResponse>): Map<Int, String> {
    val rules = settings ?: return emptyMap()
    val missing = linkedMapOf<Int, String>()
    parts.forEachIndexed { index, part ->
        if (rules.requireProject && part.projectId == null) missing[index] = "Choose a project."
        else if (part.taskId == null && part.projectId != null && requiresTask(project(part.projectId))) missing[index] = "Choose a task."
        else if (rules.requireDescription && part.description?.isBlank() == true) missing[index] = DescriptionRequired
    }
    return missing
}

/** Whether each part has a description of its own, which its project's reports and timesheet links show. */
fun describesEach(parts: List<TimeShareResponse>?): Boolean = parts != null && parts.size > 1 && parts.any { it.description != null }

/** The parts' own descriptions as one, as the server makes an entry's description of them. */
fun joinedDescriptions(parts: List<TimeShareResponse>): String =
    parts.mapNotNull { it.description?.trim() }.filter { it.isNotEmpty() }.joinToString("; ")

/**
 * Gives each part a description of its own. The one description so far goes to the largest part, as it most likely
 * describes the project the timer was on; the others start empty rather than repeat it on other projects.
 */
fun describingEach(parts: List<TimeShareResponse>, description: String): List<TimeShareResponse> {
    // The first of equal largest, as the server and the other apps choose it.
    val largest = parts.indices.fold(0) { best, index -> if (parts[index].percent > parts[best].percent) index else best }
    return parts.mapIndexed { index, part -> part.copy(description = if (index == largest) description.trim() else "") }
}

/** One description for all parts again: theirs joined, and none of their own. */
fun sharingOneDescription(parts: List<TimeShareResponse>): Pair<List<TimeShareResponse>, String> =
    parts.map { it.copy(description = null) } to joinedDescriptions(parts)

/** Whether another part than [except] already has this project and task, which a split cannot have twice. */
fun takenIn(parts: List<TimeShareResponse>, except: Int?): (String?, String?) -> Boolean = { projectId, taskId ->
    parts.withIndex().any { (index, part) -> index != except && part.projectId == projectId && part.taskId == taskId }
}

/**
 * The split in the entry editor, as on the Mac, the web and the iPhone: a bar whose dividers are dragged, a row per part
 * that opens it, and ways to add a project or share the time equally.
 */
@Composable
fun SplitSection(
    state: AppState, parts: List<TimeShareResponse>, seconds: Long, running: Boolean, errors: Map<Int, String>, enabled: Boolean,
    onChange: (List<TimeShareResponse>) -> Unit, onOpen: (Int) -> Unit, onAdd: () -> Unit,
) {
    val percents = parts.map { it.percent }
    SectionHeader("Split between projects")
    Card {
        SplitBar(state, parts, enabled, { onChange(parts.withPercents(it)) }, Modifier.padding(16.dp))
        val minutes = SplitMath.allocate((seconds / 60.0).roundToInt().toLong(), percents)
        parts.forEachIndexed { index, part ->
            RowDivider()
            Column(
                Modifier.fillMaxWidth().clickable(enabled = enabled) { onOpen(index) }.heightIn(min = 52.dp).padding(horizontal = 16.dp, vertical = 10.dp),
                verticalArrangement = Arrangement.Center,
            ) {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    ProjectDot(state.project(part.projectId)?.color)
                    Spacer(Modifier.width(8.dp))
                    Row(Modifier.weight(1f), verticalAlignment = Alignment.CenterVertically) {
                        Text(
                            state.nameOf(part), Modifier.weight(1f, fill = false), maxLines = 1, overflow = TextOverflow.Ellipsis,
                            color = if (part.projectId == null) MaterialTheme.colorScheme.onSurfaceVariant else MaterialTheme.colorScheme.onSurface,
                        )
                        if (part.isBillable) Icon(Icons.Filled.AttachMoney, "Billable", Modifier.padding(start = 2.dp).size(16.dp), tint = Spindle.colors.brandText)
                    }
                    Spacer(Modifier.width(8.dp))
                    if (!running) Text(Durations.hoursMinutes(minutes[index] * 60), Modifier.padding(end = 10.dp), style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
                    Text("${part.percent}%", Modifier.widthIn(min = 40.dp), fontWeight = FontWeight.Medium, textAlign = androidx.compose.ui.text.style.TextAlign.End)
                    Icon(Icons.AutoMirrored.Filled.KeyboardArrowRight, null, Modifier.padding(start = 4.dp).size(20.dp), tint = MaterialTheme.colorScheme.outline)
                }
                errors[index]?.let { Text(it, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.error, modifier = Modifier.padding(start = 17.dp, top = 2.dp)) }
            }
        }
        RowDivider()
        ActionRow("Add project", onAdd, enabled = enabled && parts.size < SplitMath.MAX_PARTS, trailing = {
            Icon(Icons.Filled.Add, null, tint = Spindle.colors.brandText)
        })
        RowDivider()
        ActionRow("Split equally", { onChange(parts.withPercents(SplitMath.equal(parts.size))) }, enabled = enabled && percents != SplitMath.equal(parts.size))
    }
    SectionFooter("Drag a divider to move time between projects. Tap a project to change it or take it out of the split.")
}

/**
 * One part of a split: its project, its share, whether it is billable, and its own description when the parts are
 * described one by one.
 */
@Composable
fun SplitPartPage(
    state: AppState, parts: List<TimeShareResponse>, index: Int, seconds: Long, running: Boolean,
    onChange: (List<TimeShareResponse>) -> Unit, onChooseProject: () -> Unit, onRemove: () -> Unit,
) {
    val part = parts.getOrNull(index) ?: return
    val percents = parts.map { it.percent }
    fun setPercent(value: Int) = onChange(parts.withPercents(SplitMath.setting(percents, index, value)))
    val described = part.description
    Column(Modifier.fillMaxSize().padding(top = if (described != null) 0.dp else 20.dp)) {
        if (described != null) {
            SectionHeader("Description")
            Card {
                PlainField(
                    described, { value -> onChange(parts.mapIndexed { position, it -> if (position == index) it.copy(description = value) else it }) },
                    "What did you do on ${state.project(part.projectId)?.name ?: "this part"}?", Modifier.padding(horizontal = 16.dp),
                    KeyboardOptions(capitalization = KeyboardCapitalization.Sentences), singleLine = false, label = "Description",
                )
            }
            Spacer(Modifier.height(20.dp))
        }
        Card {
            ValueRow("Project", onClick = onChooseProject, trailing = {
                val project = state.project(part.projectId)
                if (project != null) ProjectLabel(project, state.task(part.taskId), style = MaterialTheme.typography.bodyLarge)
                else Text("None", color = MaterialTheme.colorScheme.onSurfaceVariant)
            })
            RowDivider()
            SwitchRow("Billable", part.isBillable, { value -> onChange(parts.mapIndexed { position, it -> if (position == index) it.copy(isBillable = value) else it }) })
        }
        Spacer(Modifier.height(20.dp))
        Card {
            val most = 100 - (parts.size - 1) * SplitMath.MIN_PERCENT
            ValueRow("Share", trailing = {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Text("${part.percent}%", Modifier.padding(end = 12.dp), color = MaterialTheme.colorScheme.onSurfaceVariant)
                    FilledTonalIconButton({ setPercent(part.percent - 1) }, Modifier.size(40.dp), enabled = part.percent > SplitMath.MIN_PERCENT) {
                        Icon(Icons.Filled.Remove, "Less for ${state.nameOf(part)}")
                    }
                    Spacer(Modifier.width(8.dp))
                    FilledTonalIconButton({ setPercent(part.percent + 1) }, Modifier.size(40.dp), enabled = part.percent < most) {
                        Icon(Icons.Filled.Add, "More for ${state.nameOf(part)}")
                    }
                }
            })
            if (!running) {
                RowDivider()
                val minutes = SplitMath.allocate((seconds / 60.0).roundToInt().toLong(), percents)[index]
                ValueRow("Time", Durations.hoursMinutes(minutes * 60))
            }
        }
        SectionFooter("The other projects make room in proportion to their shares.")
        Spacer(Modifier.height(20.dp))
        Card { ActionRow("Remove from split", onRemove, destructive = true) }
    }
}

/**
 * The parts as segments of one bar, each as wide as its share. Dragging on the bar moves the nearest divider in 5%
 * steps, with a tick at each; TalkBack can move each divider too.
 */
@Composable
fun SplitBar(state: AppState, parts: List<TimeShareResponse>, enabled: Boolean, onPercents: (List<Int>) -> Unit, modifier: Modifier = Modifier) {
    val step = 5
    val percents = parts.map { it.percent }
    val bounds = percents.dropLast(1).runningReduce { sum, percent -> sum + percent }
    var width by remember { mutableIntStateOf(0) }
    var dragging by remember { mutableStateOf<Int?>(null) }
    val haptics = LocalHapticFeedback.current
    val fallback = MaterialTheme.colorScheme.onSurfaceVariant
    val surface = MaterialTheme.colorScheme.surfaceContainerLow
    // The gesture outlives recompositions, so it reads the latest shares through these.
    val latest by rememberUpdatedState(percents)
    val latestBounds by rememberUpdatedState(bounds)
    val report by rememberUpdatedState(onPercents)

    fun nearest(position: Double) = latestBounds.indices.minByOrNull { abs(latestBounds[it] - position) } ?: 0

    Box(
        modifier.fillMaxWidth().height(44.dp).onSizeChanged { width = it.width }
            .then(if (enabled && bounds.isNotEmpty()) Modifier.pointerInput(Unit) {
                var position = 0f
                detectHorizontalDragGestures(
                    onDragStart = { offset ->
                        position = offset.x
                        dragging = nearest(offset.x / size.width * 100.0)
                    },
                    onDragEnd = { dragging = null },
                    onDragCancel = { dragging = null },
                ) { change, amount ->
                    change.consume()
                    position += amount
                    val index = dragging ?: return@detectHorizontalDragGestures
                    val next = SplitMath.moveBoundary(latest, index, position / size.width * 100.0, step)
                    if (next != latest) {
                        haptics.performHapticFeedback(HapticFeedbackType.TextHandleMove)
                        report(next)
                    }
                }
            } else Modifier),
    ) {
        Row(Modifier.fillMaxSize().clip(RoundedCornerShape(8.dp))) {
            parts.forEachIndexed { index, part ->
                val color = state.colorOf(part) ?: fallback
                Box(Modifier.weight(maxOf(part.percent, 1).toFloat()).fillMaxHeight().background(color.copy(alpha = 0.2f))) {
                    Box(Modifier.fillMaxWidth().height(3.dp).align(Alignment.BottomCenter).background(color))
                    SegmentLabel(state, part, Modifier.align(Alignment.CenterStart))
                }
            }
        }
        val density = LocalDensity.current
        bounds.forEachIndexed { index, bound ->
            val x = with(density) { (width * bound / 100f).toDp() }
            val scale by animateFloatAsState(if (dragging == index) 1.2f else 1f, label = "divider")
            Box(
                Modifier.offset(x = x - 12.dp).width(24.dp).fillMaxHeight()
                    .semantics {
                        contentDescription = "Divider between ${state.nameOf(parts[index])} and ${state.nameOf(parts[index + 1])}"
                        stateDescription = "${state.nameOf(parts[index])} ${percents[index]}%, ${state.nameOf(parts[index + 1])} ${percents[index + 1]}%"
                        if (enabled) customActions = listOf(
                            CustomAccessibilityAction("More for ${state.nameOf(parts[index])}") { onPercents(SplitMath.moveBoundary(percents, index, (bound + step).toDouble())); true },
                            CustomAccessibilityAction("More for ${state.nameOf(parts[index + 1])}") { onPercents(SplitMath.moveBoundary(percents, index, (bound - step).toDouble())); true },
                        )
                    },
                contentAlignment = Alignment.Center,
            ) {
                Box(Modifier.width(2.dp).fillMaxHeight().background(surface))
                Box(Modifier.scale(scale).size(width = 8.dp, height = 26.dp).clip(CircleShape).background(MaterialTheme.colorScheme.onSurface).border(2.dp, surface, CircleShape))
            }
        }
    }
}

/** The project's name where it fits whole, otherwise only the share, or nothing in a sliver. */
@Composable
private fun SegmentLabel(state: AppState, part: TimeShareResponse, modifier: Modifier) {
    val style = MaterialTheme.typography.labelMedium
    val name = state.project(part.projectId)?.name ?: "No project"
    androidx.compose.foundation.layout.BoxWithConstraints(modifier.fillMaxHeight().padding(horizontal = 8.dp), contentAlignment = Alignment.CenterStart) {
        val measurer = androidx.compose.ui.text.rememberTextMeasurer()
        val room = constraints.maxWidth
        val full = "$name  ${part.percent}%"
        val text = when {
            measurer.measure(full, style.copy(fontWeight = FontWeight.SemiBold)).size.width <= room -> full
            measurer.measure("${part.percent}%", style.copy(fontWeight = FontWeight.SemiBold)).size.width <= room -> "${part.percent}%"
            else -> null
        }
        if (text != null) Text(text, style = style, fontWeight = FontWeight.SemiBold, maxLines = 1, softWrap = false)
    }
}
