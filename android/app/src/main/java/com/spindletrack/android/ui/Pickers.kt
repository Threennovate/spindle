package com.spindletrack.android.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.outlined.Label
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.Check
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.Search
import androidx.compose.material.icons.outlined.Folder
import androidx.compose.material.icons.outlined.FolderOff
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import com.spindletrack.android.data.*
import com.spindletrack.android.ui.theme.Spindle
import kotlinx.coroutines.launch
import java.time.*

// The pickers an entry's fields open, in a sheet from the timer or as a page of the editor.

/** A search field at the top of a picker. */
@Composable
fun SearchField(value: String, onChange: (String) -> Unit, placeholder: String, modifier: Modifier = Modifier) {
    Row(
        modifier.padding(horizontal = 16.dp, vertical = 8.dp).fillMaxWidth().heightIn(min = 44.dp).clip(RoundedCornerShape(12.dp)).background(Spindle.colors.chip).padding(horizontal = 12.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Icon(Icons.Filled.Search, null, Modifier.size(20.dp), tint = MaterialTheme.colorScheme.onSurfaceVariant)
        Spacer(Modifier.width(8.dp))
        PlainField(value, onChange, placeholder, Modifier.weight(1f), KeyboardOptions(autoCorrectEnabled = false, imeAction = ImeAction.Search))
        if (value.isNotEmpty()) IconButton(onClick = { onChange("") }, Modifier.size(28.dp)) {
            Icon(Icons.Filled.Close, "Clear", Modifier.size(16.dp), tint = MaterialTheme.colorScheme.onSurfaceVariant)
        }
    }
}

/** A row of a picker: what it is and a check when chosen. */
@Composable
private fun PickRow(index: Int, count: Int, checked: Boolean, onClick: (() -> Unit)?, inset: androidx.compose.ui.unit.Dp = 16.dp, content: @Composable RowScope.() -> Unit) {
    CardRow(index, count, onClick = onClick, inset = inset) {
        Row(Modifier.fillMaxWidth().heightIn(min = 50.dp).padding(horizontal = 16.dp, vertical = 10.dp), verticalAlignment = Alignment.CenterVertically) {
            Row(Modifier.weight(1f), verticalAlignment = Alignment.CenterVertically, content = content)
            if (checked) Icon(Icons.Filled.Check, "Chosen", Modifier.size(20.dp), tint = Spindle.colors.brandText)
        }
    }
}

private sealed interface ProjectRowItem {
    data class ProjectItem(val project: ProjectResponse) : ProjectRowItem
    data class TaskItem(val project: ProjectResponse, val task: TaskResponse) : ProjectRowItem
}

/**
 * Picks a project or one of its tasks, grouped by client. Searching narrows the list, and a new project can be created
 * on the spot where the workspace allows it. In a split, what another part has (`taken`) can't be chosen; picking for
 * something new (`choosingAnew`) checks nothing.
 */
@Composable
fun ProjectPicker(
    state: AppState, projectId: String?, taskId: String?, onPick: (String?, String?) -> Unit, modifier: Modifier = Modifier,
    taken: (String?, String?) -> Boolean = { _, _ -> false }, choosingAnew: Boolean = false,
) {
    val app = LocalApp.current
    val scope = rememberCoroutineScope()
    var search by remember { mutableStateOf("") }
    var creating by remember { mutableStateOf(false) }
    val term = search.trim()
    fun matches(vararg words: String) = term.isEmpty() || words.any { it.contains(term, ignoreCase = true) }
    val groups = state.projectGroups(keeping = projectId).mapNotNull { (client, projects) ->
        val clientName = client?.name.orEmpty()
        val rows = projects.flatMap { project ->
            val tasks = project.tasks.filter { (!it.isDone || it.id == taskId) && matches(it.name, project.name, clientName) && !taken(project.id, it.id) }
            if ((matches(project.name, clientName) && !taken(project.id, null)) || tasks.isNotEmpty()) listOf(ProjectRowItem.ProjectItem(project)) + tasks.map { ProjectRowItem.TaskItem(project, it) } else emptyList()
        }
        rows.takeIf { it.isNotEmpty() }?.let { (client?.name ?: "No client") to it }
    }
    Column(modifier) {
        SearchField(search, { search = it }, "Search projects, tasks or clients")
        LazyColumn(Modifier.fillMaxWidth(), contentPadding = PaddingValues(bottom = 24.dp)) {
            if (matches("No project") && !taken(null, null)) {
                item { Spacer(Modifier.height(8.dp)) }
                item {
                    PickRow(0, 1, projectId == null && !choosingAnew, { onPick(null, null) }) {
                        Icon(Icons.Outlined.FolderOff, null, Modifier.size(20.dp), tint = MaterialTheme.colorScheme.onSurfaceVariant)
                        Spacer(Modifier.width(10.dp))
                        Text("No project", color = MaterialTheme.colorScheme.onSurfaceVariant)
                    }
                }
            }
            groups.forEach { (heading, rows) ->
                item { SectionHeader(heading) }
                cardItems(rows) { index, row ->
                    when (row) {
                        // A project another part has shows only which project its tasks belong to.
                        is ProjectRowItem.ProjectItem -> {
                            val free = !taken(row.project.id, null)
                            PickRow(index, rows.size, row.project.id == projectId && taskId == null && !choosingAnew, if (free) ({ onPick(row.project.id, null) }) else null) {
                                ProjectDot(row.project.color, 11.dp)
                                Spacer(Modifier.width(10.dp))
                                Text(row.project.name, maxLines = 1, overflow = TextOverflow.Ellipsis, color = if (free) Color.Unspecified else MaterialTheme.colorScheme.onSurfaceVariant)
                            }
                        }
                        is ProjectRowItem.TaskItem -> PickRow(index, rows.size, row.task.id == taskId && !choosingAnew, { onPick(row.project.id, row.task.id) }, inset = 37.dp) {
                            Text(row.task.name, Modifier.padding(start = 21.dp), color = MaterialTheme.colorScheme.onSurfaceVariant, maxLines = 1, overflow = TextOverflow.Ellipsis)
                        }
                    }
                }
            }
            if (state.canCreateProjects && term.isNotEmpty()) {
                item { Spacer(Modifier.height(20.dp)) }
                item {
                    CardRow(0, 1, onClick = {
                        if (!creating) {
                            creating = true
                            scope.launch {
                                runCatching { app.repository.createProject(term, ProjectColors[state.projects.size % ProjectColors.size]) }
                                    .onSuccess { onPick(it.id, null) }.onFailure { app.notices.show(it, "Couldn't create the project.") }
                                creating = false
                            }
                        }
                    }) {
                        Row(Modifier.padding(16.dp), verticalAlignment = Alignment.CenterVertically) {
                            Icon(Icons.Filled.Add, null, tint = Spindle.colors.brandText)
                            Spacer(Modifier.width(10.dp))
                            Text("Create project “$term”", color = Spindle.colors.brandText)
                        }
                    }
                }
            }
            if (groups.isEmpty() && term.isEmpty() && !state.canCreateProjects) {
                item { EmptyState(Icons.Outlined.Folder, "No projects yet", "An admin can add projects on the Projects tab.") }
            }
        }
    }
}

/** Picks any number of tags; each tap adds or removes one at once. A name that doesn't exist yet can be created. */
@Composable
fun TagPicker(state: AppState, tagIds: List<String>, onChange: (List<String>) -> Unit, modifier: Modifier = Modifier) {
    val app = LocalApp.current
    val scope = rememberCoroutineScope()
    var chosen by remember { mutableStateOf(tagIds) }
    var search by remember { mutableStateOf("") }
    var creating by remember { mutableStateOf(false) }
    val name = search.trim()
    val tags = state.sortedTags.filter { name.isEmpty() || it.name.contains(name, ignoreCase = true) }
    val offersCreate = state.canCreateTags && name.isNotEmpty() && state.tags.none { it.name.equals(name, ignoreCase = true) }
    fun toggle(id: String) {
        chosen = if (id in chosen) chosen - id else chosen + id
        onChange(chosen)
    }
    Column(modifier) {
        SearchField(search, { search = it }, if (state.canCreateTags) "Search or create tags" else "Search tags")
        LazyColumn(Modifier.fillMaxWidth(), contentPadding = PaddingValues(top = 8.dp, bottom = 24.dp)) {
            cardItems(tags, key = { it.id }) { index, tag ->
                PickRow(index, tags.size, tag.id in chosen, { toggle(tag.id) }) {
                    Icon(Icons.AutoMirrored.Outlined.Label, null, Modifier.size(20.dp), tint = MaterialTheme.colorScheme.onSurfaceVariant)
                    Spacer(Modifier.width(10.dp))
                    Text(tag.name, maxLines = 1, overflow = TextOverflow.Ellipsis)
                }
            }
            if (offersCreate) {
                item { Spacer(Modifier.height(20.dp)) }
                item {
                    CardRow(0, 1, onClick = {
                        if (!creating) {
                            creating = true
                            scope.launch {
                                runCatching { app.repository.saveTag(name) }.onSuccess { search = ""; toggle(it.id) }.onFailure { app.notices.show(it, "Couldn't create the tag.") }
                                creating = false
                            }
                        }
                    }) {
                        Row(Modifier.padding(16.dp), verticalAlignment = Alignment.CenterVertically) {
                            Icon(Icons.Filled.Add, null, tint = Spindle.colors.brandText)
                            Spacer(Modifier.width(10.dp))
                            Text("Create tag “$name”", color = Spindle.colors.brandText)
                        }
                    }
                }
            }
            if (state.tags.isEmpty() && !offersCreate) {
                item { EmptyState(Icons.AutoMirrored.Outlined.Label, "No tags yet", if (state.canCreateTags) "Type a name to create the first one." else "An admin can add tags on the Projects tab.") }
            }
        }
    }
}

// MARK: - Dates and times

/** Picks a day with Material's calendar, in the account's time zone. */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun DayPickerDialog(initial: LocalDate, onPick: (LocalDate) -> Unit, onDismiss: () -> Unit, earliest: LocalDate? = null, latest: LocalDate? = null, title: String? = null) {
    val state = rememberDatePickerState(
        initialSelectedDateMillis = initial.atStartOfDay(ZoneOffset.UTC).toInstant().toEpochMilli(),
        selectableDates = object : SelectableDates {
            override fun isSelectableDate(utcTimeMillis: Long): Boolean {
                val day = Instant.ofEpochMilli(utcTimeMillis).atZone(ZoneOffset.UTC).toLocalDate()
                return (earliest == null || day >= earliest) && (latest == null || day <= latest)
            }
        },
    )
    DatePickerDialog(
        onDismissRequest = onDismiss,
        confirmButton = {
            TextButton(onClick = {
                state.selectedDateMillis?.let { onPick(Instant.ofEpochMilli(it).atZone(ZoneOffset.UTC).toLocalDate()) }
                onDismiss()
            }) { Text("OK", color = Spindle.colors.brandText) }
        },
        dismissButton = { TextButton(onClick = onDismiss) { Text("Cancel", color = Spindle.colors.brandText) } },
        colors = DatePickerDefaults.colors(containerColor = MaterialTheme.colorScheme.surfaceContainerHigh),
    ) {
        DatePicker(
            state, title = { Text(title ?: "Day", Modifier.padding(start = 24.dp, end = 12.dp, top = 16.dp)) },
            headline = {
                val day = state.selectedDateMillis?.let { Instant.ofEpochMilli(it).atZone(ZoneOffset.UTC).toLocalDate() }
                Text(day?.format(java.time.format.DateTimeFormatter.ofPattern("EEE, d MMM yyyy", java.util.Locale.ENGLISH)) ?: "", Modifier.padding(start = 24.dp, end = 12.dp, bottom = 12.dp), style = MaterialTheme.typography.headlineMedium)
            },
            colors = DatePickerDefaults.colors(
                containerColor = MaterialTheme.colorScheme.surfaceContainerHigh,
                selectedDayContainerColor = Spindle.colors.brand, selectedDayContentColor = Spindle.colors.ink,
                todayDateBorderColor = Spindle.colors.brandText, todayContentColor = Spindle.colors.brandText,
            ),
        )
    }
}

/** Picks a clock time, on the account's 12- or 24-hour clock. */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun ClockPickerDialog(initial: LocalTime, hour12: Boolean, onPick: (LocalTime) -> Unit, onDismiss: () -> Unit, title: String = "Time") {
    val state = rememberTimePickerState(initial.hour, initial.minute, is24Hour = !hour12)
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text(title) },
        text = {
            TimePicker(
                state, colors = TimePickerDefaults.colors(
                    selectorColor = Spindle.colors.brand, timeSelectorSelectedContainerColor = Spindle.colors.brand.copy(alpha = 0.35f),
                    timeSelectorSelectedContentColor = MaterialTheme.colorScheme.onSurface,
                    periodSelectorSelectedContainerColor = Spindle.colors.brand.copy(alpha = 0.35f),
                    clockDialSelectedContentColor = Spindle.colors.ink,
                ),
            )
        },
        confirmButton = { TextButton(onClick = { onPick(LocalTime.of(state.hour, state.minute)); onDismiss() }) { Text("OK", color = Spindle.colors.brandText) } },
        dismissButton = { TextButton(onClick = onDismiss) { Text("Cancel", color = Spindle.colors.brandText) } },
        containerColor = MaterialTheme.colorScheme.surfaceContainerHigh,
    )
}

/** An instant's day and time as two capsules, each opening its picker, as the iPhone's compact date pickers. */
@Composable
fun DateTimeCapsules(value: Instant, time: AccountTime, enabled: Boolean, onChange: (Instant) -> Unit, earliest: LocalDate? = null) {
    var pickingDay by remember { mutableStateOf(false) }
    var pickingClock by remember { mutableStateOf(false) }
    val local = value.atZone(time.zone)
    Row(horizontalArrangement = Arrangement.spacedBy(6.dp)) {
        Capsule(time.shortDay(local.toLocalDate()), enabled) { pickingDay = true }
        Capsule(time.clock(value), enabled) { pickingClock = true }
    }
    if (pickingDay) DayPickerDialog(local.toLocalDate(), { day -> onChange(local.with(day).toInstant()) }, { pickingDay = false }, earliest = earliest)
    if (pickingClock) ClockPickerDialog(local.toLocalTime(), time.hour12, { clock -> onChange(local.with(clock).withSecond(0).withNano(0).toInstant()) }, { pickingClock = false })
}

@Composable
private fun Capsule(text: String, enabled: Boolean, onClick: () -> Unit) {
    Text(
        text, style = MaterialTheme.typography.bodyLarge,
        color = if (enabled) MaterialTheme.colorScheme.onSurface else MaterialTheme.colorScheme.onSurfaceVariant,
        modifier = Modifier.clip(RoundedCornerShape(8.dp)).background(Spindle.colors.chip).clickable(enabled = enabled, onClick = onClick).padding(horizontal = 11.dp, vertical = 7.dp),
    )
}

