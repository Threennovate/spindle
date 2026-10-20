package com.spindletrack.android.ui

import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.gestures.detectDragGesturesAfterLongPress
import androidx.compose.foundation.gestures.detectHorizontalDragGestures
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.KeyboardArrowLeft
import androidx.compose.material.icons.automirrored.filled.KeyboardArrowRight
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.Lock
import androidx.compose.material.icons.filled.Warning
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.shadow
import androidx.compose.ui.zIndex
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.hapticfeedback.HapticFeedbackType
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.platform.LocalHapticFeedback
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.spindletrack.android.data.*
import com.spindletrack.android.ui.theme.Spindle
import java.time.Duration
import java.time.Instant
import java.time.LocalDate
import kotlin.math.abs
import kotlin.math.roundToInt

private val HourHeight = 56.dp
private val Gutter = 54.dp
private const val SnapMinutes = 15

/** A part of an entry on one day, in minutes from the day's start, with its lane among overlapping entries. */
private data class Segment(val entry: TimeEntryResponse, val start: Double, val end: Double, val lane: Int, val lanes: Int)

/** Lays out a day's entries: each clipped to the day, side by side where they overlap. */
private fun layout(entries: List<TimeEntryResponse>, day: LocalDate, time: AccountTime, now: Instant): List<Segment> {
    val dayStart = time.start(day)
    val dayEnd = time.start(day.plusDays(1))
    val clipped = entries.mapNotNull { entry ->
        val start = maxOf(entry.startInstant, dayStart)
        val end = minOf(entry.stopInstant ?: now, dayEnd)
        if (end <= start) null else Triple(entry, Duration.between(dayStart, start).seconds / 60.0, Duration.between(dayStart, end).seconds / 60.0)
    }.sortedWith(compareBy({ it.second }, { -it.third }))
    // Clusters of entries that overlap share their width; each takes the first lane free at its start.
    val result = mutableListOf<Segment>()
    var cluster = mutableListOf<Pair<Triple<TimeEntryResponse, Double, Double>, Int>>()
    var clusterEnd = -1.0
    val laneEnds = mutableListOf<Double>()
    fun flush() {
        val lanes = (cluster.maxOfOrNull { it.second } ?: 0) + 1
        cluster.forEach { (item, lane) -> result += Segment(item.first, item.second, item.third, lane, lanes) }
        cluster = mutableListOf(); laneEnds.clear()
    }
    clipped.forEach { item ->
        if (item.second >= clusterEnd) flush()
        val lane = laneEnds.indexOfFirst { it <= item.second }.let { if (it < 0) laneEnds.size else it }
        if (lane == laneEnds.size) laneEnds += item.third else laneEnds[lane] = item.third
        cluster += item to lane
        clusterEnd = maxOf(clusterEnd, item.third)
    }
    flush()
    return result
}

/** A day of time on a timeline, with its week above to move between days. */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun CalendarTab(state: AppState) {
    val app = LocalApp.current
    val time = rememberTime(state)
    val today = time.today()
    var chosen by remember { mutableStateOf<LocalDate?>(null) }
    val day = chosen ?: today
    val week = time.week(day)
    var loadError by remember { mutableStateOf<String?>(null) }

    LaunchedEffect(week.first(), state.workspaceId) {
        loadError = runCatching { app.repository.loadEntries(week.first().minusDays(1), week.last().plusDays(1)) }.exceptionOrNull()?.message
    }

    fun addEntry() {
        val target = if (state.isLocked(day)) today else day
        val nowMinute = Duration.between(time.start(today), Instant.now()).toMinutes() - 7
        val startMinute = (nowMinute / SnapMinutes * SnapMinutes).coerceIn(0, 23 * 60)
        val start = time.start(target).plusSeconds(startMinute * 60)
        app.editing = EntryEditing(newRange = start to start.plusSeconds(3600))
    }

    Scaffold(
        containerColor = MaterialTheme.colorScheme.background, contentWindowInsets = WindowInsets(0),
        topBar = {
            CenterBar(
                time.dayHeading(day, today),
                navigation = { TextButton(onClick = { chosen = null }, enabled = day != today, colors = ButtonDefaults.textButtonColors(contentColor = Spindle.colors.brandText)) { Text("Today") } },
                actions = { IconButton(onClick = ::addEntry, enabled = state.readOnlyOrganization == null) { Icon(Icons.Filled.Add, "Add a time entry") } },
            )
        },
    ) { padding ->
        Column(Modifier.padding(padding).fillMaxSize()) {
            WeekStrip(state, time, week, day, today, select = { chosen = if (it == today) null else it }, move = { weeks -> chosen = day.plusWeeks(weeks.toLong()) })
            HorizontalDivider(thickness = 0.5.dp, color = Spindle.colors.hairline)
            state.readOnlyOrganization?.let { organization ->
                ReadOnlyNotice(organization, Modifier.background(MaterialTheme.colorScheme.error.copy(alpha = 0.08f)).padding(horizontal = 16.dp, vertical = 10.dp))
                HorizontalDivider(thickness = 0.5.dp, color = Spindle.colors.hairline)
            }
            loadError?.let {
                Row(Modifier.padding(8.dp).fillMaxWidth(), horizontalArrangement = Arrangement.Center, verticalAlignment = Alignment.CenterVertically) {
                    Icon(Icons.Filled.Warning, null, Modifier.size(14.dp), tint = MaterialTheme.colorScheme.onSurfaceVariant)
                    Spacer(Modifier.width(6.dp))
                    Text(it, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                }
            }
            key(day) {
                DayTimeline(state, time, day, today, Modifier.pointerInput(day) {
                    var across = 0f
                    detectHorizontalDragGestures(onDragStart = { across = 0f }, onDragEnd = {
                        if (abs(across) > 80.dp.toPx()) chosen = day.plusDays(if (across < 0) 1 else -1).takeIf { it != today }
                    }) { _, amount -> across += amount }
                })
            }
        }
    }
}

/** The week of the day shown: each day with the time tracked on it; tapping one shows it. */
@Composable
private fun WeekStrip(state: AppState, time: AccountTime, week: List<LocalDate>, selected: LocalDate, today: LocalDate, select: (LocalDate) -> Unit, move: (Int) -> Unit) {
    val now = clockNow(state.running != null, 60_000)
    Row(Modifier.fillMaxWidth().padding(horizontal = 4.dp, vertical = 8.dp), verticalAlignment = Alignment.CenterVertically) {
        IconButton(onClick = { move(-1) }, Modifier.size(32.dp, 44.dp)) { Icon(Icons.AutoMirrored.Filled.KeyboardArrowLeft, "Previous week", tint = Spindle.colors.brandText) }
        week.forEach { day ->
            val seconds = state.entries.sumOf { daySeconds(it, day, time.zone, now) }
            val isSelected = day == selected
            val isToday = day == today
            Column(
                Modifier.weight(1f).clip(RoundedCornerShape(10.dp)).clickable { select(day) }.padding(vertical = 2.dp)
                    .semantics(mergeDescendants = true) { contentDescription = "${time.longDay(day)}, ${if (seconds >= 60) Durations.hoursMinutes(seconds) else "nothing tracked"}" },
                horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.spacedBy(3.dp),
            ) {
                Text(time.pattern(day, "EEEEE"), style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                Box(
                    Modifier.size(34.dp).clip(CircleShape).background(
                        when { isSelected && isToday -> Spindle.colors.brand; isSelected -> MaterialTheme.colorScheme.onSurface; else -> Color.Transparent }
                    ),
                    contentAlignment = Alignment.Center,
                ) {
                    Text(
                        day.dayOfMonth.toString(), style = MaterialTheme.typography.bodyLarge.copy(fontWeight = if (isSelected) FontWeight.Bold else FontWeight.Medium),
                        color = when { isSelected && isToday -> Spindle.colors.ink; isSelected -> MaterialTheme.colorScheme.background; isToday -> Spindle.colors.brandText; else -> MaterialTheme.colorScheme.onSurface },
                    )
                }
                Text(if (seconds >= 60) Durations.hoursMinutes(seconds) else " ", style = MaterialTheme.typography.labelSmall.copy(fontSize = 10.sp), color = MaterialTheme.colorScheme.onSurfaceVariant, maxLines = 1)
            }
        }
        IconButton(onClick = { move(1) }, Modifier.size(32.dp, 44.dp)) { Icon(Icons.AutoMirrored.Filled.KeyboardArrowRight, "Next week", tint = Spindle.colors.brandText) }
    }
}

/**
 * The hours of one day, with its entries as blocks. Tapping a block opens it, tapping empty space adds an hour there,
 * and a long press on a block lets it be dragged to another time.
 */
@Composable
private fun DayTimeline(state: AppState, time: AccountTime, day: LocalDate, today: LocalDate, modifier: Modifier = Modifier) {
    val app = LocalApp.current
    val haptics = LocalHapticFeedback.current
    val density = LocalDensity.current
    val now = clockNow(true, 60_000)
    val segments = remember(state.entries, day, now) { layout(state.entries, day, time, now) }
    val scroll = rememberScrollState()
    val perMinute = with(density) { HourHeight.toPx() } / 60f
    var lifted by remember { mutableStateOf<String?>(null) }
    var dragMinutes by remember { mutableFloatStateOf(0f) }

    // Opens on the day's first entry, or at 7:00; today it keeps now in view, opening an hour before it at the latest.
    LaunchedEffect(day) {
        val first = segments.minOfOrNull { it.start } ?: (7 * 60.0)
        var minute = first
        if (day == today) {
            val nowMinute = Duration.between(time.start(day), Instant.now()).toMinutes().toDouble()
            val shown = scroll.viewportSize / perMinute
            minute = minOf(maxOf(minute, nowMinute - shown + 90), nowMinute - 60)
        }
        scroll.scrollTo(((minute / 60).toInt().coerceIn(0, 23) * 60 * perMinute).roundToInt())
    }

    fun move(segment: Segment, minutes: Float) {
        val entry = segment.entry
        // Snapped to quarter hours, and kept within the day.
        val length = segment.end - segment.start
        val target = ((segment.start + minutes) / SnapMinutes).roundToInt() * SnapMinutes.toDouble()
        val shift = (target.coerceIn(0.0, maxOf(0.0, 24 * 60 - length)) - segment.start).roundToInt()
        if (shift == 0) return
        val start = entry.startInstant.plusSeconds(shift * 60L)
        val stop = entry.stopInstant?.plusSeconds(shift * 60L)
        if (state.isLocked(start)) { app.notices.show("That day is locked; the entry can't move there.", error = true); return }
        app.launch {
            runCatching { app.repository.saveEntry(entry.draft().copy(start = start.toString(), stop = stop?.toString()), entry) }
                .onFailure { app.notices.show(it, "Couldn't move the time entry.") }
        }
    }

    BoxWithConstraints(modifier.fillMaxSize()) {
        val laneArea = maxWidth - Gutter - 8.dp
        Box(Modifier.fillMaxSize().verticalScroll(scroll, enabled = lifted == null).padding(vertical = 12.dp)) {
            Box(
                Modifier.fillMaxWidth().height(HourHeight * 24).pointerInput(day, segments) {
                    detectTapGestures { point ->
                        if (point.x < Gutter.toPx()) return@detectTapGestures
                        if (state.readOnlyOrganization != null) { app.notices.show(ReadOnlyMessage, error = true); return@detectTapGestures }
                        if (state.isLocked(day)) { app.notices.show("This day is locked; nothing can be added to it.", error = true); return@detectTapGestures }
                        val minute = ((point.y / perMinute) / SnapMinutes).toInt() * SnapMinutes
                        val start = time.start(day).plusSeconds(minute.coerceIn(0, 23 * 60 + 45) * 60L)
                        app.editing = EntryEditing(newRange = start to minOf(start.plusSeconds(3600), time.start(day.plusDays(1))))
                    }
                },
            ) {
                Hours(time, day, if (day == today) Duration.between(time.start(day), now).toMinutes().toDouble() else null)
                segments.forEach { segment ->
                    val laneWidth = laneArea / segment.lanes
                    val isLifted = lifted == segment.entry.id
                    val offset by animateFloatAsState(if (isLifted) dragMinutes else 0f, label = "drag")
                    EntryBlock(
                        state, time, segment, isLifted,
                        Modifier.zIndex(if (isLifted) 1f else 0f).offset(x = Gutter + laneWidth * segment.lane, y = HourHeight * ((segment.start + (if (isLifted) dragMinutes else offset)) / 60).toFloat())
                            .width(maxOf(laneWidth - 2.dp, 20.dp))
                            .height(maxOf(HourHeight * ((segment.end - segment.start) / 60).toFloat() - 1.dp, 18.dp))
                            .pointerInput(segment.entry.id) {
                                detectTapGestures(onTap = { app.editing = EntryEditing(segment.entry) })
                            }
                            .pointerInput(segment.entry.id, segment.start) {
                                detectDragGesturesAfterLongPress(
                                    onDragStart = {
                                        // Locked and running entries only open, as on the iPhone and the web.
                                        if (state.isLocked(segment.entry.startInstant) || segment.entry.isRunning) return@detectDragGesturesAfterLongPress
                                        lifted = segment.entry.id; dragMinutes = 0f
                                        haptics.performHapticFeedback(HapticFeedbackType.LongPress)
                                    },
                                    onDragEnd = { if (lifted == segment.entry.id) move(segment, dragMinutes); lifted = null; dragMinutes = 0f },
                                    onDragCancel = { lifted = null; dragMinutes = 0f },
                                ) { change, amount ->
                                    if (lifted == segment.entry.id) { change.consume(); dragMinutes += amount.y / perMinute }
                                }
                            },
                    )
                }
                if (day == today) NowLine(time, Duration.between(time.start(day), now).toMinutes().toDouble(), now)
            }
        }
    }
}

@Composable
private fun Hours(time: AccountTime, day: LocalDate, nowMinute: Double?) {
    val hairline = Spindle.colors.hairline
    Column {
        (0 until 24).forEach { hour ->
            Row(Modifier.height(HourHeight).fillMaxWidth()) {
                // The now label takes the place of an hour it would cover.
                val hidden = hour == 0 || (nowMinute != null && abs(hour * 60 - nowMinute) <= 20)
                Text(
                    if (hidden) "" else time.clock(time.start(day).plusSeconds(hour * 3600L)),
                    style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant,
                    modifier = Modifier.width(Gutter - 6.dp).offset(y = (-7).dp), textAlign = androidx.compose.ui.text.style.TextAlign.End,
                )
                Spacer(Modifier.width(6.dp))
                Column(Modifier.fillMaxHeight()) {
                    HorizontalDivider(thickness = 0.5.dp, color = hairline)
                    Spacer(Modifier.height(HourHeight / 2 - 0.5.dp))
                    HorizontalDivider(thickness = 0.5.dp, color = hairline.copy(alpha = hairline.alpha * 0.45f))
                }
            }
        }
    }
}

/** Now, in marigold: the colour of the middle bar of the mark, with its time in the hour column. */
@Composable
private fun NowLine(time: AccountTime, minute: Double, now: Instant) {
    val y = HourHeight * (minute / 60).toFloat()
    Box(Modifier.fillMaxWidth().offset(y = y - 4.dp).height(8.dp)) {
        Row(Modifier.padding(start = Gutter - 4.dp).fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
            Box(Modifier.size(8.dp).clip(CircleShape).background(Spindle.colors.brand))
            Box(Modifier.weight(1f).height(2.dp).background(Spindle.colors.brand))
        }
    }
    Box(Modifier.width(Gutter - 4.dp).offset(y = y - 9.dp), contentAlignment = Alignment.CenterEnd) {
        Text(
            time.clock(now), style = MaterialTheme.typography.labelSmall.copy(fontWeight = FontWeight.SemiBold), color = Spindle.colors.ink,
            modifier = Modifier.clip(CircleShape).background(Spindle.colors.brand).padding(horizontal = 5.dp, vertical = 1.dp),
        )
    }
}

/**
 * An entry on the timeline, tinted in its project's colour and held by a hairline in it. A split entry takes the colour
 * of its largest part; a thread along its bottom shows every part.
 */
@Composable
private fun EntryBlock(state: AppState, time: AccountTime, segment: Segment, lifted: Boolean, modifier: Modifier) {
    val entry = segment.entry
    val dark = Spindle.colors.dark
    val project = state.project(entry.projectId)
    val color = project?.let { projectColor(it.color) } ?: Color.Gray
    val minutes = segment.end - segment.start
    val parts = if (entry.isSplit) entry.parts.sortedByDescending { it.percent } else null
    // Half an hour has room for the thread under the text; shorter split blocks show dots by the description.
    val threaded = parts != null && minutes >= 30
    val locked = state.isLocked(entry.startInstant)
    val muted = MaterialTheme.colorScheme.onSurfaceVariant
    val shape = RoundedCornerShape(6.dp)
    Box(
        modifier.then(if (lifted) Modifier.shadow(8.dp, shape) else Modifier).clip(shape)
            .background(MaterialTheme.colorScheme.background).background(color.copy(alpha = if (lifted) 0.32f else if (dark) 0.2f else 0.15f))
            .border(1.dp, color.copy(alpha = 0.3f), shape)
            .semantics {
                contentDescription = "${entry.description.ifEmpty { "No description" }}, ${parts?.let { "split: ${state.describe(it)}" } ?: project?.name ?: "no project"}, " +
                    "${time.clock(entry.startInstant)} – ${entry.stopInstant?.let(time::clock) ?: "now"}${if (locked) ", locked" else ""}"
            },
    ) {
        Column(Modifier.padding(start = 6.dp, end = 6.dp, top = if (minutes >= 30) 3.dp else 1.dp, bottom = if (threaded) 10.dp else 1.dp)) {
            val small = MaterialTheme.typography.labelSmall.copy(fontSize = 11.sp, lineHeight = 13.sp)
            Row(verticalAlignment = Alignment.CenterVertically) {
                if (entry.isRunning) { Box(Modifier.size(5.dp).clip(CircleShape).background(Spindle.colors.brand)); Spacer(Modifier.width(3.dp)) }
                if (locked) { Icon(Icons.Filled.Lock, null, Modifier.size(9.dp), tint = muted); Spacer(Modifier.width(3.dp)) }
                Text(
                    entry.description.ifEmpty { "No description" }, style = small.copy(fontWeight = FontWeight.SemiBold),
                    color = if (entry.description.isEmpty()) muted else MaterialTheme.colorScheme.onSurface,
                    maxLines = if (minutes >= (if (parts == null) 45 else 75)) 2 else 1, overflow = TextOverflow.Ellipsis, modifier = Modifier.weight(1f, fill = false),
                )
                if (parts != null && !threaded) { Spacer(Modifier.width(2.dp).weight(1f)); SplitDots(state, parts, 7.dp) }
            }
            if (minutes >= (if (parts == null) 30 else 45)) {
                if (parts != null) Text(splitText(state, parts, dark, muted), style = small, maxLines = 1, overflow = TextOverflow.Ellipsis)
                else Text(project?.name ?: "No project", style = small.copy(fontWeight = FontWeight.Medium), color = readableColor(project?.color, dark) ?: muted, maxLines = 1)
            }
            if (minutes >= 60) Text("${time.clock(entry.startInstant)} – ${entry.stopInstant?.let(time::clock) ?: "now"}", style = small, color = muted, maxLines = 1)
        }
        if (parts != null && threaded) SplitThread(state, parts, Modifier.align(Alignment.BottomCenter).padding(horizontal = 6.dp).padding(bottom = 5.dp))
    }
}

