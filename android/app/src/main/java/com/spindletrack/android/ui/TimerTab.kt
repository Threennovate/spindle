package com.spindletrack.android.ui

import androidx.compose.animation.animateColorAsState
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.BasicTextField
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.outlined.Label
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.AttachMoney
import androidx.compose.material.icons.filled.CloudOff
import androidx.compose.material.icons.filled.ContentCopy
import androidx.compose.material.icons.filled.Delete
import androidx.compose.material.icons.filled.Edit
import androidx.compose.material.icons.filled.Lock
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material.icons.filled.Sync
import androidx.compose.material.icons.filled.Visibility
import androidx.compose.material.icons.filled.Warning
import androidx.compose.material.icons.outlined.Folder
import androidx.compose.material.icons.outlined.HourglassEmpty
import androidx.compose.material.icons.outlined.Timer
import androidx.compose.material3.*
import androidx.compose.material3.pulltorefresh.PullToRefreshBox
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.focus.onFocusChanged
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.input.nestedscroll.nestedScroll
import androidx.compose.ui.platform.LocalFocusManager
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.input.KeyboardCapitalization
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import com.spindletrack.android.data.*
import com.spindletrack.android.ui.theme.Spindle
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import java.time.Instant
import java.time.LocalDate

/** The timer on top, and the person's entries below, a week at a time and grouped by day. */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun TimerTab(state: AppState) {
    val app = LocalApp.current
    val options = LocalLaunchOptions.current
    val time = rememberTime(state)
    val scope = rememberCoroutineScope()
    val scroll = TopAppBarDefaults.exitUntilCollapsedScrollBehavior()
    val focusManager = LocalFocusManager.current
    var draft by remember { mutableStateOf(EntryDraft(description = options.description.orEmpty())) }
    var focused by remember { mutableStateOf(false) }
    var suggestions by remember { mutableStateOf<List<TimeEntrySuggestion>>(emptyList()) }
    var weeks by remember { mutableIntStateOf(1) }
    var refreshing by remember { mutableStateOf(false) }
    var loaded by remember { mutableStateOf(false) }
    var deleting by remember { mutableStateOf<TimeEntryResponse?>(null) }
    var recovering by remember { mutableStateOf(false) }
    val today = time.today()
    val since = time.week(today).first().minusWeeks((weeks - 1).toLong())

    LaunchedEffect(state.workspaceId, weeks) {
        app.repository.loadEntries(since, today.plusDays(1))
        loaded = true
    }
    LaunchedEffect(draft.description, focused, state.running == null) {
        if (!focused || state.running != null) return@LaunchedEffect
        delay(150)
        suggestions = runCatching { app.repository.suggestions(draft.description.trim()) }.getOrDefault(suggestions)
    }
    var openedFromOptions by remember { mutableStateOf(false) }
    LaunchedEffect(loaded, state.entries.isNotEmpty()) {
        if (options.edit == null || !loaded || openedFromOptions || (options.edit != "new" && state.entries.isEmpty())) return@LaunchedEffect
        openedFromOptions = true
        app.editing = when (options.edit) {
            "new" -> EntryEditing(newRange = defaultRange(state, time))
            "running" -> state.running?.let { EntryEditing(it) }
            else -> state.entries.firstOrNull { it.stop != null }?.let { EntryEditing(it) }
        }
    }

    fun start(details: EntryDraft) {
        val used = draft
        app.launch { if (app.start(details) && draft == used) draft = EntryDraft() }
    }

    Scaffold(
        Modifier.nestedScroll(scroll.nestedScrollConnection),
        containerColor = MaterialTheme.colorScheme.background,
        topBar = {
            LargeBar("Timer", scroll, navigation = { SyncStatus(state) }, actions = {
                IconButton(onClick = { app.editing = EntryEditing(newRange = defaultRange(state, time)) }, enabled = state.readOnlyOrganization == null) { Icon(Icons.Filled.Add, "Add a time entry") }
            })
        },
        contentWindowInsets = WindowInsets(0),
    ) { padding ->
        PullToRefreshBox(refreshing, { scope.launch { refreshing = true; app.repository.refresh(); app.repository.loadEntries(since, today.plusDays(1)); refreshing = false } }, Modifier.padding(padding)) {
            LazyColumn(Modifier.fillMaxSize(), contentPadding = PaddingValues(top = 4.dp, bottom = 24.dp)) {
                state.readOnlyOrganization?.let { organization ->
                    item(key = "readOnly") {
                        ReadOnlyNotice(organization, Modifier.padding(horizontal = 16.dp).clip(RoundedCornerShape(16.dp))
                            .background(MaterialTheme.colorScheme.error.copy(alpha = 0.08f)).padding(16.dp))
                        Spacer(Modifier.height(12.dp))
                    }
                }
                item(key = "timer") {
                    TimerCard(state, time, draft, { draft = it }, focused, { focused = it }, ::start)
                }
                if (state.failedChanges.isNotEmpty()) item(key = "failed") {
                    Spacer(Modifier.height(12.dp))
                    Card(Modifier.clickable { recovering = true }) {
                        Row(Modifier.padding(16.dp), verticalAlignment = Alignment.CenterVertically) {
                            Icon(Icons.Filled.Warning, null, tint = MaterialTheme.colorScheme.error)
                            Spacer(Modifier.width(12.dp))
                            Column(Modifier.weight(1f)) {
                                val count = state.failedChanges.size
                                Text(if (count == 1) "1 change wasn't accepted" else "$count changes weren't accepted", style = MaterialTheme.typography.titleSmall)
                                Text("Tap to fix, try again or discard.", style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                            }
                        }
                    }
                }
                if (focused && state.running == null && suggestions.isNotEmpty()) {
                    item { SectionHeader("Previous entries") }
                    cardItems(suggestions) { index, suggestion ->
                        CardRow(index, suggestions.size, onClick = {
                            start(EntryDraft(suggestion.description, suggestion.projectId, suggestion.taskId, suggestion.tagIds, suggestion.isBillable, suggestion.shares.takeIf { it.size > 1 }?.map { it.toRequest() }))
                            focusManager.clearFocus()
                        }) {
                            Column(Modifier.padding(horizontal = 16.dp, vertical = 12.dp), verticalArrangement = Arrangement.spacedBy(4.dp)) {
                                Text(suggestion.description.ifEmpty { "No description" }, color = if (suggestion.description.isEmpty()) MaterialTheme.colorScheme.onSurfaceVariant else Color.Unspecified, maxLines = 1, overflow = TextOverflow.Ellipsis)
                                if (suggestion.shares.size > 1) SplitLabel(state, suggestion.shares)
                                else state.project(suggestion.projectId)?.let { ProjectLabel(it, state.task(suggestion.taskId), state.client(it.clientId)) }
                            }
                        }
                    }
                } else {
                    val days = state.entries.filter { time.day(it.startInstant) >= since }.groupBy { time.day(it.startInstant) }.toSortedMap(compareByDescending { it })
                    item(key = "week") {
                        val now = clockNow(state.running != null)
                        val week = time.week(today)
                        val total = state.entries.filter { time.day(it.startInstant) in week }.sumOf { it.seconds(now) }
                        SectionHeader("This week") { Text(time.duration(total, state.running != null)) }
                    }
                    if (!loaded && days.isEmpty()) {
                        item { Box(Modifier.fillMaxWidth().padding(32.dp), contentAlignment = Alignment.Center) { CircularProgressIndicator(Modifier.size(24.dp), strokeWidth = 2.5.dp) } }
                    } else if (days.isEmpty()) {
                        item {
                            Card {
                                EmptyState(Icons.Outlined.Timer, "Nothing tracked yet this week", "Type what you're working on and tap the play button. To add time you forgot to track, tap +.")
                            }
                        }
                    }
                    days.forEach { (day, entries) ->
                        item(key = "day-$day") {
                            val now = clockNow(entries.any { it.isRunning })
                            SectionHeader(time.dayHeading(day, today)) { Text(time.duration(entries.sumOf { it.seconds(now) }, entries.any { it.isRunning })) }
                        }
                        cardItems(entries, key = { it.id }) { index, entry ->
                            EntryRowItem(state, time, entry, index, entries.size, onDelete = { deleting = entry })
                        }
                    }
                    item(key = "more") {
                        Column(Modifier.fillMaxWidth().padding(top = 20.dp), horizontalAlignment = Alignment.CenterHorizontally) {
                            BrandLink("Load the Week Before", { weeks += 1 })
                            Text("Showing entries since ${time.longDay(since)}", style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                        }
                    }
                }
            }
        }
    }
    deleting?.let { entry ->
        val duration = time.duration(entry.seconds())
        ConfirmDialog(
            "Delete this time entry?",
            if (entry.description.isEmpty()) "The entry and its $duration will be removed." else "“${entry.description}” and its $duration will be removed.",
            "Delete", onConfirm = { app.launch { app.delete(entry) } }, onDismiss = { deleting = null },
        )
    }
    if (recovering) FailedChangesSheet(state) { recovering = false }
}

/** A new entry added by hand starts where the last entry today ended, or half an hour ago, and ends now. */
fun defaultRange(state: AppState, time: AccountTime): Pair<Instant, Instant> {
    val end = Instant.now().truncatedTo(java.time.temporal.ChronoUnit.MINUTES)
    val today = time.day(end)
    val lastStop = state.entries.mapNotNull { it.stopInstant }.filter { time.day(it) == today && it <= end }.maxOrNull()
    val start = lastStop?.takeIf { java.time.Duration.between(it, end).seconds >= 60 } ?: end.minusSeconds(30 * 60)
    return start to end
}

/** Whether changes reach the server: nothing when all is well, otherwise that the phone is offline or catching up. */
@Composable
private fun SyncStatus(state: AppState) {
    var explaining by remember { mutableStateOf(false) }
    val waiting = state.pendingCount
    when {
        state.offline -> TextButton(onClick = { explaining = true }, colors = ButtonDefaults.textButtonColors(contentColor = MaterialTheme.colorScheme.onSurfaceVariant)) {
            Icon(Icons.Filled.CloudOff, null, Modifier.size(16.dp))
            Spacer(Modifier.width(6.dp))
            Text(if (waiting > 0) "Offline · $waiting" else "Offline", style = MaterialTheme.typography.labelLarge)
        }
        waiting > 0 && state.failedChanges.isEmpty() -> Row(Modifier.padding(horizontal = 16.dp), verticalAlignment = Alignment.CenterVertically) {
            Icon(Icons.Filled.Sync, null, Modifier.size(16.dp), tint = MaterialTheme.colorScheme.onSurfaceVariant)
            Spacer(Modifier.width(6.dp))
            Text("Syncing…", style = MaterialTheme.typography.labelLarge, color = MaterialTheme.colorScheme.onSurfaceVariant)
        }
    }
    if (explaining) AlertDialog(
        onDismissRequest = { explaining = false }, title = { Text("You're offline") },
        text = {
            Text(
                if (waiting > 0) "Spindle can't reach the server. ${if (waiting == 1) "1 change is" else "$waiting changes are"} kept on this phone and sync when it's back."
                else "Spindle can't reach the server. You can go on tracking; changes sync when it's back."
            )
        },
        confirmButton = { TextButton(onClick = { explaining = false }) { Text("OK", color = Spindle.colors.brandText) } },
        containerColor = MaterialTheme.colorScheme.surfaceContainerHigh,
    )
}

/** The card at the top of the timer: what's running and a button to stop it, or what to start next. */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun TimerCard(state: AppState, time: AccountTime, draft: EntryDraft, setDraft: (EntryDraft) -> Unit, focused: Boolean, setFocused: (Boolean) -> Unit, onStart: (EntryDraft) -> Unit) {
    val app = LocalApp.current
    val options = LocalLaunchOptions.current
    val focusManager = LocalFocusManager.current
    val running = state.running
    var runningText by remember(running?.id) { mutableStateOf(running?.description.orEmpty()) }
    var choosingProject by remember { mutableStateOf(options.picker == "project") }
    var choosingTags by remember { mutableStateOf(options.picker == "tags") }
    var editingStart by remember { mutableStateOf(false) }
    // Follows changes made elsewhere, e.g. on another device, unless the person is typing.
    LaunchedEffect(running?.description) { if (!focused) runningText = running?.description.orEmpty() }

    val projectId = running?.projectId ?: draft.projectId
    val taskId = running?.taskId ?: draft.taskId
    val tagIds = running?.tagIds ?: draft.tagIds
    val billable = running?.parts?.any { it.isBillable } ?: draft.isBillable
    fun setProject(project: String?, task: String?) {
        val projectBillable = state.project(project)?.isBillable
        if (running != null) app.launch { app.updateRunning { it.copy(projectId = project, taskId = task, isBillable = projectBillable ?: it.isBillable, shares = null) } }
        else setDraft(draft.copy(projectId = project, taskId = task, isBillable = projectBillable ?: false))
    }
    fun setTags(ids: List<String>) {
        if (running != null) app.launch { app.updateRunning { it.copy(tagIds = ids) } } else setDraft(draft.copy(tagIds = ids))
    }
    fun setBillable(value: Boolean) {
        if (running != null) app.launch { app.updateRunning { it.copy(isBillable = value, shares = it.shares?.map { part -> part.copy(isBillable = value) }) } }
        else setDraft(draft.copy(isBillable = value))
    }
    fun saveRunningDescription() {
        val current = running ?: return
        val trimmed = runningText.trim()
        if (trimmed != current.description) app.launch { app.updateRunning { it.copy(description = trimmed) } }
    }

    Card {
        Column(Modifier.padding(start = 16.dp, end = 14.dp, top = 14.dp, bottom = 14.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                BasicTextField(
                    if (running != null) runningText else draft.description,
                    { if (running != null) runningText = it else setDraft(draft.copy(description = it)) },
                    Modifier.weight(1f).padding(end = 12.dp).semantics { contentDescription = if (running != null) "Description of the running entry" else "What are you working on?" }.onFocusChanged { change ->
                        if (focused && !change.isFocused) saveRunningDescription()
                        setFocused(change.isFocused)
                    },
                    textStyle = MaterialTheme.typography.bodyLarge.copy(color = MaterialTheme.colorScheme.onSurface),
                    cursorBrush = SolidColor(Spindle.colors.brandText),
                    keyboardOptions = KeyboardOptions(capitalization = KeyboardCapitalization.Sentences, imeAction = if (running != null) ImeAction.Done else ImeAction.Go),
                    keyboardActions = KeyboardActions(onAny = {
                        focusManager.clearFocus()
                        if (running == null) onStart(draft)
                    }),
                    maxLines = 3,
                    decorationBox = { inner ->
                        Box {
                            val placeholder = if (running != null) "Add description" else "What are you working on?"
                            if ((if (running != null) runningText else draft.description).isEmpty()) Text(placeholder, style = MaterialTheme.typography.bodyLarge, color = MaterialTheme.colorScheme.onSurfaceVariant.copy(alpha = 0.75f))
                            inner()
                        }
                    },
                )
                if (running != null) {
                    val now = clockNow()
                    Text(
                        Durations.clock(running.seconds(now)), style = MaterialTheme.typography.headlineSmall,
                        modifier = Modifier.clip(RoundedCornerShape(8.dp)).clickable { editingStart = true }.padding(horizontal = 4.dp, vertical = 2.dp),
                    )
                    Spacer(Modifier.width(10.dp))
                    StopButton({ focusManager.clearFocus(); app.launch { if (app.stop()) app.notices.show("Timer stopped") } })
                } else {
                    StartButton({ focusManager.clearFocus(); onStart(draft) }, enabled = state.readOnlyOrganization == null)
                }
            }
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp), verticalAlignment = Alignment.CenterVertically) {
                val project = state.project(projectId)
                if (running?.isSplit == true) {
                    ChipButton({ choosingProject = true }, Modifier.weight(1f, fill = false), label = "Split: ${state.describe(running.parts)}") {
                        // The split's largest project, which the entry mirrors, and how many others share it.
                        val largest = running.parts.maxBy { it.percent }
                        SplitDots(state, running.parts)
                        Spacer(Modifier.width(6.dp))
                        Text(
                            state.project(largest.projectId)?.name ?: "No project", maxLines = 1, overflow = TextOverflow.Ellipsis, fontWeight = FontWeight.Medium,
                            color = readableColor(state.project(largest.projectId)?.color, Spindle.colors.dark) ?: MaterialTheme.colorScheme.onSurfaceVariant,
                        )
                        Text(" +${running.parts.size - 1}", color = MaterialTheme.colorScheme.onSurfaceVariant)
                    }
                } else ChipButton({ choosingProject = true }, Modifier.weight(1f, fill = false), label = project?.let { "Project: ${it.name}" } ?: "Choose project") {
                    if (project != null) ProjectLabel(project, state.task(taskId))
                    else {
                        Icon(Icons.Outlined.Folder, null, Modifier.size(18.dp), tint = MaterialTheme.colorScheme.onSurfaceVariant)
                        Spacer(Modifier.width(6.dp))
                        Text("Project", color = MaterialTheme.colorScheme.onSurfaceVariant)
                    }
                }
                val tagNames = tagIds.mapNotNull { state.tag(it)?.name }.joinToString(", ")
                ChipButton({ choosingTags = true }, Modifier.weight(1f, fill = false), label = if (tagIds.isEmpty()) "Choose tags" else "Tags: $tagNames") {
                    Icon(Icons.AutoMirrored.Outlined.Label, null, Modifier.size(18.dp), tint = if (tagIds.isEmpty()) MaterialTheme.colorScheme.onSurfaceVariant else Spindle.colors.brandText)
                    if (tagIds.isNotEmpty()) {
                        Spacer(Modifier.width(6.dp))
                        Text(tagNames, color = Spindle.colors.brandText, maxLines = 1, overflow = TextOverflow.Ellipsis)
                    }
                }
                val billableColor by animateColorAsState(if (billable) Spindle.colors.brandText else MaterialTheme.colorScheme.onSurfaceVariant, label = "billable")
                ChipButton({ setBillable(!billable) }, label = if (billable) "Billable, on" else "Billable, off") {
                    Icon(Icons.Filled.AttachMoney, null, Modifier.size(19.dp), tint = billableColor)
                }
                Spacer(Modifier.weight(0.001f))
                PomodoroChip(state, running)
            }
        }
    }

    if (choosingProject) PickerSheet("Project", { choosingProject = false }) {
        ProjectPicker(state, projectId, taskId, { project, task -> setProject(project, task); choosingProject = false }, Modifier.fillMaxHeight(0.85f))
    }
    if (choosingTags) PickerSheet("Tags", { choosingTags = false }, actions = { BarAction("Done", { choosingTags = false }, prominent = true) }) {
        TagPicker(state, tagIds, ::setTags, Modifier.fillMaxHeight(0.7f))
    }
    if (editingStart && running != null) StartTimeDialogs(running, time, onDismiss = { editingStart = false }) { start ->
        app.launch { app.updateRunning { it.copy(start = start.toString()) } }
    }
}

/** Corrects when the running timer started, e.g. after forgetting to start it: the day, then the time. */
@Composable
private fun StartTimeDialogs(entry: TimeEntryResponse, time: AccountTime, onDismiss: () -> Unit, onSave: (Instant) -> Unit) {
    val started = entry.startInstant.atZone(time.zone)
    var day by remember { mutableStateOf<LocalDate?>(null) }
    if (day == null) {
        DayPickerDialog(started.toLocalDate(), { day = it }, { if (day == null) onDismiss() }, latest = time.today(), title = "Started on")
    } else {
        ClockPickerDialog(started.toLocalTime(), time.hour12, { clock ->
            val start = day!!.atTime(clock).atZone(time.zone).toInstant()
            onSave(minOf(start, Instant.now()))
        }, onDismiss, title = "Started at")
    }
}

/** The Pomodoro's countdown: the focus left in this interval. */
@Composable
private fun PomodoroChip(state: AppState, running: TimeEntryResponse?) {
    val preferences = state.me?.preferences ?: return
    if (!preferences.pomodoroEnabled || running == null || preferences.pomodoroMinutes <= 0) return
    val now = clockNow()
    val focus = preferences.pomodoroMinutes * 60L
    val elapsed = maxOf(0L, java.time.Duration.between(running.startInstant, now).seconds)
    val left = focus - elapsed % focus
    Row(verticalAlignment = Alignment.CenterVertically) {
        Icon(Icons.Outlined.HourglassEmpty, null, Modifier.size(16.dp), tint = MaterialTheme.colorScheme.onSurfaceVariant)
        Spacer(Modifier.width(4.dp))
        Text("%d:%02d".format(left / 60, left % 60), style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
    }
}

/** One entry: what it was, its project and tags, when, and for how long; swiped to continue or delete. */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun EntryRowItem(state: AppState, time: AccountTime, entry: TimeEntryResponse, index: Int, count: Int, onDelete: () -> Unit) {
    val app = LocalApp.current
    val locked = state.isLocked(entry.startInstant)
    var menu by remember { mutableStateOf(false) }
    val swipe = rememberSwipeToDismissBoxState(confirmValueChange = { value ->
        when (value) {
            SwipeToDismissBoxValue.StartToEnd -> if (!entry.isRunning) app.launch { app.continueEntry(entry) }
            SwipeToDismissBoxValue.EndToStart -> if (!locked) onDelete()
            SwipeToDismissBoxValue.Settled -> {}
        }
        false
    })
    val shape = rowShape(index, count)
    Box(Modifier.padding(horizontal = 16.dp).clip(shape)) {
        SwipeToDismissBox(
            swipe,
            enableDismissFromStartToEnd = !entry.isRunning,
            enableDismissFromEndToStart = !locked,
            backgroundContent = {
                val continuing = swipe.dismissDirection == SwipeToDismissBoxValue.StartToEnd
                Row(
                    Modifier.fillMaxSize().background(if (continuing) Spindle.colors.brand else MaterialTheme.colorScheme.error).padding(horizontal = 24.dp),
                    verticalAlignment = Alignment.CenterVertically, horizontalArrangement = if (continuing) Arrangement.Start else Arrangement.End,
                ) {
                    if (continuing) {
                        Icon(Icons.Filled.PlayArrow, null, tint = Spindle.colors.ink)
                        Spacer(Modifier.width(6.dp))
                        Text("Continue", color = Spindle.colors.ink, style = MaterialTheme.typography.labelLarge)
                    } else {
                        Text("Delete", color = MaterialTheme.colorScheme.onError, style = MaterialTheme.typography.labelLarge)
                        Spacer(Modifier.width(6.dp))
                        Icon(Icons.Filled.Delete, null, tint = MaterialTheme.colorScheme.onError)
                    }
                }
            },
        ) {
            Box(Modifier.background(MaterialTheme.colorScheme.surfaceContainerLow).combinedClickableCompat({ app.editing = EntryEditing(entry) }, { menu = true })) {
                EntryRow(state, time, entry, locked)
                if (index > 0) RowDivider()
                DropdownMenu(menu, { menu = false }) {
                    if (!entry.isRunning) DropdownMenuItem({ Text("Continue") }, { menu = false; app.launch { app.continueEntry(entry) } }, leadingIcon = { Icon(Icons.Filled.PlayArrow, null) })
                    DropdownMenuItem({ Text(if (locked) "View" else "Edit") }, { menu = false; app.editing = EntryEditing(entry) }, leadingIcon = { Icon(if (locked) Icons.Filled.Visibility else Icons.Filled.Edit, null) })
                    if (!entry.isRunning && !locked) DropdownMenuItem({ Text("Duplicate") }, { menu = false; app.launch { app.duplicate(entry) } }, leadingIcon = { Icon(Icons.Filled.ContentCopy, null) })
                    if (!locked) DropdownMenuItem({ Text("Delete", color = MaterialTheme.colorScheme.error) }, { menu = false; onDelete() }, leadingIcon = { Icon(Icons.Filled.Delete, null, tint = MaterialTheme.colorScheme.error) })
                }
            }
        }
    }
}

@Composable
fun EntryRow(state: AppState, time: AccountTime, entry: TimeEntryResponse, locked: Boolean) {
    val project = state.project(entry.projectId)
    val tags = entry.tagIds.mapNotNull { state.tag(it) }
    val muted = MaterialTheme.colorScheme.onSurfaceVariant
    Column(Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 12.dp), verticalArrangement = Arrangement.spacedBy(5.dp)) {
        Row(verticalAlignment = Alignment.Top) {
            Text(
                entry.description.ifEmpty { "No description" }, style = MaterialTheme.typography.bodyLarge,
                color = if (entry.description.isEmpty()) muted else MaterialTheme.colorScheme.onSurface,
                maxLines = 2, overflow = TextOverflow.Ellipsis, modifier = Modifier.weight(1f).padding(end = 8.dp),
            )
            if (entry.isRunning) {
                val now = clockNow()
                Text(Durations.clock(entry.seconds(now)), style = MaterialTheme.typography.bodyLarge.copy(fontWeight = FontWeight.SemiBold), color = Spindle.colors.brandText)
            } else {
                Text(time.duration(entry.seconds()), style = MaterialTheme.typography.bodyLarge.copy(fontWeight = FontWeight.Medium))
            }
        }
        Row(verticalAlignment = Alignment.CenterVertically) {
            Row(Modifier.weight(1f), verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                if (entry.isSplit) SplitLabel(state, entry.parts, MaterialTheme.typography.bodyMedium, Modifier.weight(1f, fill = false))
                else if (project != null) ProjectLabel(project, state.task(entry.taskId), state.client(project.clientId), MaterialTheme.typography.bodyMedium, Modifier.weight(1f, fill = false))
                tags.firstOrNull()?.let { TagChip(if (tags.size > 1) "${it.name} +${tags.size - 1}" else it.name) }
            }
            if (locked) Icon(Icons.Filled.Lock, "Locked", Modifier.padding(start = 6.dp).size(14.dp), tint = muted)
            if (entry.parts.any { it.isBillable }) Icon(Icons.Filled.AttachMoney, "Billable", Modifier.padding(start = 6.dp).size(16.dp), tint = muted)
            Text(
                "${time.clock(entry.startInstant)} – ${entry.stopInstant?.let(time::clock) ?: "now"}",
                style = MaterialTheme.typography.bodyMedium, color = muted, maxLines = 1, modifier = Modifier.padding(start = 6.dp),
            )
        }
    }
}

/** Changes the server didn't accept: each can be fixed in the editor, sent again or dropped. */
@Composable
private fun FailedChangesSheet(state: AppState, onDismiss: () -> Unit) {
    val app = LocalApp.current
    var discarding by remember { mutableStateOf<PendingChange?>(null) }
    LaunchedEffect(state.failedChanges.isEmpty()) { if (state.failedChanges.isEmpty()) onDismiss() }
    PickerSheet("Changes not accepted", onDismiss) {
        LazyColumn(contentPadding = PaddingValues(bottom = 32.dp)) {
            item { SectionFooter("The server didn't accept these. Fix the entry, send it again, or drop the change to keep what the server has.", Modifier.padding(bottom = 12.dp)) }
            cardItems(state.failedChanges, key = { it.id }) { index, change ->
                val entry = change.optimistic?.let { runCatching { app.repository.json.decodeFromString(TimeEntryResponse.serializer(), it) }.getOrNull() }
                    ?: state.entries.find { it.id == change.entryId }
                CardRow(index, state.failedChanges.size) {
                    Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(4.dp)) {
                        Text(entry?.description?.ifBlank { "Time entry" } ?: "Time entry", style = MaterialTheme.typography.titleSmall)
                        Text(change.error ?: "The server couldn't accept this change.", style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.error)
                        Row(horizontalArrangement = Arrangement.spacedBy(4.dp)) {
                            BarAction("Try Again", { app.launch { runCatching { app.repository.retryFailedChange(change.id) }.onFailure { app.notices.show(it, "Couldn't send the change.") } } })
                            if (entry != null && change.method != "DELETE") BarAction("Edit", {
                                app.launch {
                                    if (entry.workspaceId != state.workspaceId) app.repository.switchWorkspace(entry.workspaceId)
                                    app.editing = EntryEditing(entry)
                                }
                                onDismiss()
                            })
                            TextButton(onClick = { discarding = change }) { Text("Discard", color = MaterialTheme.colorScheme.error) }
                        }
                    }
                }
            }
        }
    }
    discarding?.let { change ->
        ConfirmDialog(
            "Discard this change?", "It's removed from this phone. The server's version of the entry stays, if there is one.", "Discard",
            onConfirm = { app.launch { runCatching { app.repository.discardFailedChange(change.id) }.onFailure { app.notices.show(it, "Couldn't discard the change.") } } },
            onDismiss = { discarding = null },
        )
    }
}
