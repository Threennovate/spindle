package com.spindletrack.android.ui

import androidx.compose.animation.AnimatedContent
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.slideInHorizontally
import androidx.compose.animation.slideOutHorizontally
import androidx.compose.animation.togetherWith
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.CallSplit
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.semantics.clearAndSetSemantics
import androidx.compose.ui.text.input.KeyboardCapitalization
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import com.spindletrack.android.data.*
import com.spindletrack.android.ui.theme.Spindle
import kotlinx.coroutines.launch
import java.time.Duration
import java.time.Instant

private enum class EditorPage { Form, Project, Tags, SplitWith, Part, PartProject }

/** Edits an entry, or adds one for a stretch of time. Entries on locked days are shown, not edited. */
@Composable
fun EntryEditor(state: AppState, editing: EntryEditing, onDismiss: () -> Unit) {
    val app = LocalApp.current
    val time = rememberTime(state)
    val scope = rememberCoroutineScope()
    val entry = editing.entry
    val running = entry?.isRunning == true
    val stopping = editing.stopping && running
    val locked = entry?.let { state.isLocked(it.startInstant) } ?: false

    var description by remember { mutableStateOf(entry?.description.orEmpty()) }
    var projectId by remember { mutableStateOf(entry?.projectId) }
    var taskId by remember { mutableStateOf(entry?.taskId) }
    var tagIds by remember { mutableStateOf(entry?.tagIds.orEmpty()) }
    var billable by remember { mutableStateOf(entry?.isBillable ?: false) }
    // Two or more parts while the time is split between projects; null while it all goes to one.
    var parts by remember { mutableStateOf(entry?.takeIf { it.isSplit }?.parts) }
    // The part open on its own page.
    var partIndex by remember { mutableIntStateOf(0) }
    var start by remember { mutableStateOf(entry?.startInstant ?: editing.newRange?.first ?: Instant.now().minusSeconds(1800)) }
    var end by remember { mutableStateOf(if (entry != null) entry.stopInstant else editing.newRange?.second ?: Instant.now()) }
    var errors by remember { mutableStateOf(mapOf<String, String>()) }
    var formError by remember { mutableStateOf<String?>(null) }
    var saving by remember { mutableStateOf(false) }
    var confirmingDelete by remember { mutableStateOf(false) }
    var page by remember { mutableStateOf(EditorPage.Form) }
    val project = state.project(projectId)
    val taskRequired = state.requiresTask(project)
    // Whether each part of the split has a description of its own, in place of the entry's one.
    val perPart = describesEach(parts)
    // Asks the description field of a part for the keyboard once it is shown.
    val partFocus = remember { List(SplitMath.MAX_PARTS) { FocusRequester() } }
    var focusPart by remember { mutableStateOf<Int?>(null) }
    LaunchedEffect(focusPart) { focusPart?.let { runCatching { partFocus[it].requestFocus() }; focusPart = null } }

    // Parts described one by one make the entry's description of theirs, as the server does. A split says whether its
    // parts are described one by one, so going back to one description sticks; other entries leave it out.
    fun currentDraft(stop: Instant?) = EntryDraft(
        description = if (perPart) joinedDescriptions(parts.orEmpty()) else description.trim(), projectId = projectId, taskId = taskId,
        tagIds = tagIds, isBillable = billable, describeParts = parts?.let { perPart }, start = start.toString(), stop = stop?.toString(),
    ).splitting(parts?.map { it.copy(description = it.description?.trim()) })

    /** What the draft is missing; a split's parts each get their own, a part's description under its field. */
    fun missing(draft: EntryDraft): Map<String, String> {
        val split = parts ?: return state.missingDetails(draft)
        val inParts = state.missingInParts(split).mapKeys { (index, message) -> if (message == DescriptionRequired) "shares[$index].description" else "shares[$index]" }
        return state.missingDetails(draft) - setOfNotNull("projectId", "taskId", "description".takeIf { perPart }) + inParts
    }

    fun setPartDescription(index: Int, value: String) {
        parts = parts?.mapIndexed { position, part -> if (position == index) part.copy(description = value) else part }
    }

    /** Each project gets its own description, and the first one still to be written is ready for typing. */
    fun describePerPart() {
        val described = describingEach(parts ?: return, description)
        parts = described
        focusPart = described.indexOfFirst { it.description.isNullOrEmpty() }.takeIf { it >= 0 } ?: 0
    }

    /** One description for all parts again: theirs joined. */
    fun describeAllAsOne() {
        val (shared, joined) = sharingOneDescription(parts ?: return)
        parts = shared
        description = joined
        errors = errors.filterKeys { !it.endsWith("].description") }
    }
    LaunchedEffect(Unit) { if (stopping && entry != null) errors = missing(currentDraft(Instant.now())) }

    /** Changes the split; one part left is that part's project again, described as that part was. */
    fun setParts(next: List<TimeShareResponse>) {
        if (next.size > 1) { parts = next; return }
        next.singleOrNull()?.let { only ->
            projectId = only.projectId; taskId = only.taskId; billable = only.isBillable
            only.description?.let { description = it }
        }
        parts = null
        if (page == EditorPage.Part || page == EditorPage.PartProject) page = EditorPage.Form
    }

    fun removePart(index: Int) {
        val current = parts ?: return
        val next = current.toMutableList().apply { removeAt(index) }
        page = EditorPage.Form
        setParts(next.withPercents(SplitMath.removing(current.map { it.percent }, index)))
    }

    /** Adds a project to the split, or begins one: what was chosen keeps half the time, the project picked gets the other half. */
    fun addToSplit(newProject: String?, newTask: String?) {
        // A part added while the others have their own descriptions gets one too, to be written.
        val added = TimeShareResponse(newProject, newTask, state.project(newProject)?.isBillable ?: false, 0, if (perPart) "" else null)
        val current = parts
        parts = if (current == null) {
            listOf(TimeShareResponse(projectId, taskId, billable, 50), added.copy(percent = 50))
        } else {
            val next = SplitMath.adding(current.map { it.percent })
            current.withPercents(next.dropLast(1)) + added.copy(percent = next.last())
        }
    }

    fun save(stopNow: Boolean) {
        val stop = if (stopNow) Instant.now() else end
        if (stop != null && stop < start) { errors = mapOf("stop" to "The end must not be before the start."); return }
        val draft = currentDraft(stop)
        // A running entry may lack details for now; a finished one must have what the workspace requires.
        errors = if (stop != null) missing(draft) else emptyMap()
        if (errors.isNotEmpty()) return
        saving = true; formError = null
        scope.launch {
            runCatching { app.repository.saveEntry(draft, entry) }
                .onSuccess { if (stopNow) app.notices.show("Timer stopped"); onDismiss() }
                .onFailure { formError = it.message ?: "Couldn't save the entry." }
            saving = false
        }
    }

    val title = when {
        entry == null -> "New Time Entry"
        locked -> "Time Entry"
        stopping -> "Stop Timer"
        running -> "Running Entry"
        else -> "Edit Time Entry"
    }
    FullScreenEditor(
        title = when (page) {
            EditorPage.Form -> title
            EditorPage.Project, EditorPage.PartProject -> "Project"
            EditorPage.Tags -> "Tags"
            EditorPage.SplitWith -> if (parts == null) "Split With" else "Add to Split"
            EditorPage.Part -> parts?.getOrNull(partIndex)?.let { state.nameOf(it) } ?: ""
        },
        onDismiss = {
            when (page) {
                EditorPage.Form -> onDismiss()
                EditorPage.PartProject -> page = EditorPage.Part
                else -> page = EditorPage.Form
            }
        },
        confirm = when {
            page == EditorPage.Tags || page == EditorPage.Part -> "Done"
            page != EditorPage.Form -> null
            locked -> "Done"
            else -> if (entry == null) "Add" else if (stopping) "Stop" else "Save"
        },
        onConfirm = { if (page != EditorPage.Form) page = EditorPage.Form else if (locked) onDismiss() else save(stopping) },
        confirmEnabled = !saving,
        dismissLabel = if (stopping) "Keep running" else "Cancel",
        back = page != EditorPage.Form,
    ) {
        AnimatedContent(page, label = "page", transitionSpec = {
            if (targetState.ordinal > initialState.ordinal) (slideInHorizontally { it / 3 } + fadeIn()) togetherWith fadeOut()
            else fadeIn() togetherWith (slideOutHorizontally { it / 3 } + fadeOut())
        }) { shown ->
            when (shown) {
                EditorPage.Project -> {
                    androidx.activity.compose.BackHandler { page = EditorPage.Form }
                    ProjectPicker(state, projectId, taskId, { newProject, newTask ->
                        projectId = newProject; taskId = newTask
                        billable = state.project(newProject)?.isBillable ?: billable
                        // All of the time on one project keeps what was written for each.
                        if (perPart) description = joinedDescriptions(parts.orEmpty())
                        parts = null
                        page = EditorPage.Form
                    }, choosingAnew = parts != null)
                }
                EditorPage.Tags -> {
                    androidx.activity.compose.BackHandler { page = EditorPage.Form }
                    TagPicker(state, tagIds, { tagIds = it })
                }
                EditorPage.SplitWith -> {
                    androidx.activity.compose.BackHandler { page = EditorPage.Form }
                    val taken = parts?.let { takenIn(it, null) } ?: { p: String?, t: String? -> p == projectId && t == taskId }
                    ProjectPicker(state, null, null, { newProject, newTask ->
                        addToSplit(newProject, newTask)
                        page = EditorPage.Form
                    }, taken = taken, choosingAnew = true)
                }
                EditorPage.Part -> {
                    androidx.activity.compose.BackHandler { page = EditorPage.Form }
                    parts?.let { split ->
                        SplitPartPage(
                            state, split, partIndex, Duration.between(start, end ?: Instant.now()).seconds, end == null,
                            onChange = { setParts(it) }, onChooseProject = { page = EditorPage.PartProject }, onRemove = { removePart(partIndex) },
                        )
                    }
                }
                EditorPage.PartProject -> {
                    androidx.activity.compose.BackHandler { page = EditorPage.Part }
                    val split = parts
                    split?.getOrNull(partIndex)?.let { part ->
                        ProjectPicker(state, part.projectId, part.taskId, { newProject, newTask ->
                            setParts(split.mapIndexed { index, it ->
                                if (index == partIndex) it.copy(projectId = newProject, taskId = newTask, isBillable = state.project(newProject)?.isBillable ?: it.isBillable) else it
                            })
                            page = EditorPage.Part
                        }, taken = takenIn(split, partIndex))
                    }
                }
                EditorPage.Form -> Column(Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(bottom = 32.dp)) {
                    val banner: @Composable (String, Boolean) -> Unit = { text, error -> FormBanner(text, error, Modifier.padding(horizontal = 16.dp).padding(top = 12.dp)) }
                    if (locked) banner(state.lockMessage(), false)
                    if (stopping) banner("Add the details this workspace requires, then stop the timer.", false)
                    formError?.let { banner(it, true) }

                    // The entry's description, or one per project while the parts are described one by one. On a split,
                    // a switch says which, as one description shows on every project's reports and timesheet links.
                    SectionHeader(required(if (perPart) "Description per project" else "Description", state.settings?.requireDescription == true && !locked))
                    Card {
                        val split = parts
                        if (split != null && perPart) {
                            split.forEachIndexed { index, part ->
                                if (index > 0) RowDivider()
                                PartDescription(
                                    state, part, { setPartDescription(index, it) }, errors["shares[$index].description"], !locked,
                                    Modifier.padding(horizontal = 16.dp).padding(top = 12.dp), Modifier.focusRequester(partFocus[index]),
                                )
                            }
                        } else {
                            PlainField(
                                description, { description = it }, "Description", Modifier.padding(horizontal = 16.dp),
                                KeyboardOptions(capitalization = KeyboardCapitalization.Sentences), enabled = !locked, singleLine = false,
                            )
                        }
                        if (split != null && !locked) {
                            RowDivider()
                            SwitchRow("Describe each project", perPart, { if (it) describePerPart() else describeAllAsOne() })
                        }
                    }
                    errors["description"]?.let { SectionFooter(it, color = MaterialTheme.colorScheme.error) }
                        ?: parts?.takeIf { !locked }?.let {
                            SectionFooter(
                                if (perPart) "Each project's reports and timesheet links show only its own description."
                                else "Shown on all ${it.size} projects, timesheet links included.",
                            )
                        }

                    parts?.let { split ->
                        val now = clockNow(running)
                        SplitSection(
                            state, split, Duration.between(start, end ?: now).seconds, end == null,
                            errors.mapNotNull { (key, message) -> Regex("""shares\[(\d+)]""").matchEntire(key)?.let { it.groupValues[1].toInt() to message } }.toMap(),
                            enabled = !locked, onChange = { setParts(it) },
                            onOpen = { partIndex = it; page = EditorPage.Part }, onAdd = { page = EditorPage.SplitWith },
                        )
                    }

                    Spacer(Modifier.height(20.dp))
                    Card {
                        val projectLabel = required(if (taskRequired) "Project and task" else "Project", (state.settings?.requireProject == true || taskRequired) && !locked)
                        ValueRow(projectLabel, onClick = if (locked) null else ({ page = EditorPage.Project }), trailing = {
                            when {
                                parts != null -> Text("Split", color = MaterialTheme.colorScheme.onSurfaceVariant)
                                project != null -> ProjectLabel(project, state.task(taskId), style = MaterialTheme.typography.bodyLarge)
                                else -> Text("None", color = MaterialTheme.colorScheme.onSurfaceVariant)
                            }
                        })
                        RowDivider()
                        if (parts == null && !locked) {
                            ActionRow("Split between projects", { page = EditorPage.SplitWith }, trailing = {
                                Icon(Icons.AutoMirrored.Filled.CallSplit, null, tint = Spindle.colors.brandText)
                            })
                            RowDivider()
                        }
                        ValueRow(
                            required("Tags", state.settings?.requireTag == true && !locked),
                            if (tagIds.isEmpty()) "None" else tagIds.mapNotNull { state.tag(it)?.name }.joinToString(", "),
                            onClick = if (locked) null else ({ page = EditorPage.Tags }),
                        )
                        RowDivider()
                        // Billable applies to every part of a split entry.
                        SwitchRow("Billable", parts?.any { it.isBillable } ?: billable, { value ->
                            billable = value
                            parts = parts?.map { it.copy(isBillable = value) }
                        }, enabled = !locked)
                    }
                    (errors["projectId"] ?: errors["taskId"] ?: errors["tagIds"])?.let { SectionFooter(it, color = MaterialTheme.colorScheme.error) }
                        ?: parts?.let { SectionFooter("Choosing a project puts all of the time on it.") }

                    Spacer(Modifier.height(20.dp))
                    Card {
                        val earliest = if (locked) null else state.firstOpenDay
                        ValueRow("Start", trailing = {
                            DateTimeCapsules(start, time, !locked, { newStart ->
                                // A new start keeps the end where it is; another day, or a start past the end, moves the whole entry.
                                val currentEnd = end
                                if (currentEnd != null && (time.day(newStart) != time.day(start) || newStart > currentEnd)) end = newStart.plus(Duration.between(start, currentEnd))
                                start = newStart
                            }, earliest)
                        })
                        RowDivider()
                        val currentEnd = end
                        if (currentEnd != null) ValueRow("End", trailing = { DateTimeCapsules(currentEnd, time, !locked, { end = it }, time.day(start)) })
                        else ValueRow("End", "Running")
                        RowDivider()
                        val now = clockNow(running)
                        ValueRow("Duration", Durations.clock(Duration.between(start, end ?: now).seconds))
                    }
                    errors["stop"]?.let { SectionFooter(it, color = MaterialTheme.colorScheme.error) }

                    if (running && !stopping && !locked) {
                        Spacer(Modifier.height(20.dp))
                        Card { ActionRow("Stop Timer", { save(true) }, enabled = !saving) }
                    }
                    if (entry != null && !locked) {
                        Spacer(Modifier.height(20.dp))
                        Card { ActionRow("Delete Time Entry", { confirmingDelete = true }, destructive = true) }
                    }
                }
            }
        }
    }
    if (confirmingDelete && entry != null) {
        val subject = if (entry.description.isEmpty()) "The entry" else "“${entry.description}”"
        ConfirmDialog(
            "Delete this time entry?", "$subject and its ${Durations.clock(Duration.between(start, end ?: Instant.now()).seconds)} will be removed.", "Delete",
            onConfirm = { app.launch { app.delete(entry); onDismiss() } }, onDismiss = { confirmingDelete = false },
        )
    }
}

private fun required(label: String, isRequired: Boolean) = if (isRequired) "$label (required)" else label

/** A part's own description, under its project's name, with what it lacks. */
@Composable
private fun PartDescription(
    state: AppState, part: TimeShareResponse, onChange: (String) -> Unit, error: String?, enabled: Boolean,
    modifier: Modifier, fieldModifier: Modifier,
) {
    Column(modifier) {
        Row(Modifier.clearAndSetSemantics {}, verticalAlignment = Alignment.CenterVertically) {
            ProjectDot(state.project(part.projectId)?.color, 8.dp)
            Spacer(Modifier.width(6.dp))
            Text(state.nameOf(part), style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant, maxLines = 1, overflow = TextOverflow.Ellipsis)
        }
        PlainField(
            part.description.orEmpty(), onChange, "What did you do on ${state.project(part.projectId)?.name ?: "this part"}?", fieldModifier,
            KeyboardOptions(capitalization = KeyboardCapitalization.Sentences), enabled = enabled, singleLine = false,
            label = "Description for ${state.nameOf(part)}",
        )
        error?.let { Text(it, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.error, modifier = Modifier.padding(bottom = 12.dp)) }
    }
}

