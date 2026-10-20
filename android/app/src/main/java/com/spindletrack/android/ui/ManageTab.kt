package com.spindletrack.android.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.KeyboardArrowRight
import androidx.compose.material.icons.automirrored.outlined.Label
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.AttachMoney
import androidx.compose.material.icons.filled.Check
import androidx.compose.material.icons.filled.CheckCircle
import androidx.compose.material.icons.filled.FilterList
import androidx.compose.material.icons.filled.Inventory2
import androidx.compose.material.icons.filled.Lock
import androidx.compose.material.icons.filled.MoreVert
import androidx.compose.material.icons.outlined.Business
import androidx.compose.material.icons.outlined.Circle
import androidx.compose.material.icons.outlined.Folder
import androidx.compose.material.icons.outlined.Inventory2
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.input.KeyboardCapitalization
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextDecoration
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import com.spindletrack.android.data.*
import com.spindletrack.android.ui.theme.Spindle
import kotlinx.coroutines.launch
import kotlinx.serialization.json.*

private enum class ManagePart(val title: String) { Projects("Projects"), Clients("Clients"), Tags("Tags") }

/** The workspace's projects, clients and tags. */
@Composable
fun ManageTab(state: AppState) {
    val options = LocalLaunchOptions.current
    var part by remember { mutableStateOf(ManagePart.entries.firstOrNull { it.name.equals(options.manage, true) } ?: ManagePart.Projects) }
    var openProject by remember { mutableStateOf<String?>(null) }
    var creating by remember { mutableStateOf(options.project == "new") }
    LaunchedEffect(state.projects.isNotEmpty()) {
        if (options.project != null && options.project != "new" && openProject == null) openProject = state.projects.firstOrNull { it.name == options.project }?.id
    }

    val shown = openProject
    if (shown != null) {
        ProjectDetail(state, shown) { openProject = null }
    } else {
        when (part) {
            ManagePart.Projects -> ProjectList(state, { part = it }, { openProject = it }, { creating = true })
            ManagePart.Clients -> ClientList(state) { part = it }
            ManagePart.Tags -> TagList(state) { part = it }
        }
    }
    if (creating) ProjectEditor(state, null) { creating = false }
}

/** The bar of the Projects tab's lists, with the switch between them under it. */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun ManageScaffold(part: ManagePart, choose: (ManagePart) -> Unit, navigation: @Composable () -> Unit = {}, actions: @Composable RowScope.() -> Unit = {}, content: @Composable ColumnScope.() -> Unit) {
    Scaffold(containerColor = MaterialTheme.colorScheme.background, contentWindowInsets = WindowInsets(0), topBar = { CenterBar(part.title, navigation, actions) }) { padding ->
        Column(Modifier.padding(padding).fillMaxSize()) {
            Box(Modifier.padding(horizontal = 16.dp, vertical = 6.dp)) { Segmented(ManagePart.entries.map { it to it.title }, part, choose) }
            content()
        }
    }
}

/** A small capsule beside a name, e.g. "Archived". */
@Composable
private fun Badge(text: String) {
    Text(text, style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.clip(CircleShape).background(Spindle.colors.chip).padding(horizontal = 6.dp, vertical = 1.dp))
}

// MARK: - Projects

@Composable
private fun ProjectList(state: AppState, choose: (ManagePart) -> Unit, open: (String) -> Unit, create: () -> Unit) {
    val app = LocalApp.current
    var status by remember { mutableStateOf("active") }
    var clientFilter by remember { mutableStateOf("") }
    var search by remember { mutableStateOf("") }
    var deleting by remember { mutableStateOf<ProjectResponse?>(null) }
    val term = search.trim()
    val projects = state.projects.filter { project ->
        (status == "all" || (status == "archived") == (project.archivedAt != null)) &&
            (clientFilter.isEmpty() || if (clientFilter == "none") project.clientId == null else project.clientId == clientFilter) &&
            (term.isEmpty() || project.name.contains(term, true) || state.client(project.clientId)?.name?.contains(term, true) == true)
    }.sortedBy { it.name.lowercase() }
    val filtered = status != "active" || clientFilter.isNotEmpty()

    ManageScaffold(
        ManagePart.Projects, choose,
        navigation = {
            var menu by remember { mutableStateOf(false) }
            Box {
                IconButton(onClick = { menu = true }) { Icon(Icons.Filled.FilterList, "Filter projects", tint = if (filtered) Spindle.colors.brandText else Spindle.colors.brandText.copy(alpha = 0.8f)) }
                DropdownMenu(menu, { menu = false }) {
                    Text("Status", style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.padding(horizontal = 12.dp, vertical = 6.dp))
                    listOf("active" to "Active", "archived" to "Archived", "all" to "All").forEach { (value, title) ->
                        DropdownMenuItem({ Text(title) }, { status = value; menu = false }, trailingIcon = if (status == value) ({ Icon(Icons.Filled.Check, null, tint = Spindle.colors.brandText) }) else null)
                    }
                    HorizontalDivider()
                    Text("Client", style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.padding(horizontal = 12.dp, vertical = 6.dp))
                    (listOf("" to "All clients", "none" to "No client") + state.clients.sortedBy { it.name.lowercase() }.map { it.id to it.name }).forEach { (value, title) ->
                        DropdownMenuItem({ Text(title) }, { clientFilter = value; menu = false }, trailingIcon = if (clientFilter == value) ({ Icon(Icons.Filled.Check, null, tint = Spindle.colors.brandText) }) else null)
                    }
                }
            }
        },
        actions = { if (state.canCreateProjects) IconButton(onClick = create) { Icon(Icons.Filled.Add, "New project") } },
    ) {
        SearchField(search, { search = it }, "Search projects or clients")
        if (filtered) Row(Modifier.padding(horizontal = 32.dp, vertical = 2.dp), verticalAlignment = Alignment.CenterVertically) {
            Text(listOfNotNull(status.takeIf { it != "active" }?.replaceFirstChar { it.uppercase() }, clientFilter.takeIf { it.isNotEmpty() }?.let { id -> if (id == "none") "No client" else state.client(id)?.name }).joinToString(" · "),
                style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.weight(1f))
            BrandLink("Show All", { status = "active"; clientFilter = "" }, style = MaterialTheme.typography.bodySmall)
        }
        if (projects.isEmpty()) {
            EmptyState(Icons.Outlined.Folder, if (term.isEmpty() && status == "active") "No projects yet" else "No matching projects",
                if (state.projects.isEmpty()) "Projects group your time, and tasks within them." else "Try another search or filter.")
        } else LazyColumn(contentPadding = PaddingValues(top = 8.dp, bottom = 24.dp)) {
            cardItems(projects, key = { it.id }) { index, project ->
                var menu by remember { mutableStateOf(false) }
                CardRow(index, projects.size, onClick = { open(project.id) }, onLongClick = if (project.canManage) ({ menu = true }) else null, inset = 35.dp) {
                    ProjectRow(project, state.client(project.clientId)?.name)
                    DropdownMenu(menu, { menu = false }) {
                        DropdownMenuItem({ Text(if (project.archivedAt == null) "Archive" else "Restore") }, {
                            menu = false
                            app.launch {
                                runCatching { app.repository.setProjectArchived(project.id, project.archivedAt == null) }
                                    .onSuccess { app.notices.show(if (project.archivedAt == null) "“${project.name}” archived" else "“${project.name}” restored") }
                                    .onFailure { app.notices.show(it, "Couldn't change the project.") }
                            }
                        }, leadingIcon = { Icon(Icons.Outlined.Inventory2, null) })
                        DropdownMenuItem({ Text("Delete", color = MaterialTheme.colorScheme.error) }, { menu = false; deleting = project })
                    }
                }
            }
        }
    }
    deleting?.let { DeleteProjectDialog(it) { deleting = null } }
}

@Composable
private fun DeleteProjectDialog(project: ProjectResponse, after: () -> Unit = {}, onDismiss: () -> Unit) {
    val app = LocalApp.current
    ConfirmDialog(
        "Delete “${project.name}”?",
        if (project.trackedSeconds > 0) "Its tasks are deleted. The ${Durations.hoursMinutes(project.trackedSeconds)} tracked on it stay, without a project. To keep the project for reports, archive it instead."
        else "Its tasks are deleted too.",
        "Delete Project",
        onConfirm = {
            app.launch {
                runCatching { app.repository.deleteProject(project.id) }
                    .onSuccess { app.notices.show("“${project.name}” deleted"); after() }
                    .onFailure { app.notices.show(it, "Couldn't delete the project.") }
            }
        },
        onDismiss = onDismiss,
    )
}

@Composable
private fun ProjectRow(project: ProjectResponse, clientName: String?) {
    val muted = MaterialTheme.colorScheme.onSurfaceVariant
    Row(Modifier.fillMaxWidth().padding(start = 16.dp, end = 10.dp, top = 12.dp, bottom = 12.dp), verticalAlignment = Alignment.CenterVertically) {
        Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(5.dp)) {
            Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
                ProjectDot(project.color, 11.dp)
                Spacer(Modifier.width(8.dp))
                Text(project.name, style = MaterialTheme.typography.bodyLarge.copy(fontWeight = FontWeight.Medium), maxLines = 1, overflow = TextOverflow.Ellipsis, modifier = Modifier.weight(1f, fill = false))
                if (project.isPrivate) Icon(Icons.Filled.Lock, "Private", Modifier.padding(start = 6.dp).size(13.dp), tint = muted)
                if (project.isBillable) Icon(Icons.Filled.AttachMoney, "Billable", Modifier.padding(start = 4.dp).size(15.dp), tint = muted)
                if (project.archivedAt != null) { Spacer(Modifier.width(6.dp)); Badge("Archived") }
                Spacer(Modifier.weight(1f).widthIn(min = 8.dp))
                Text(Durations.hoursMinutes(project.trackedSeconds), style = MaterialTheme.typography.bodyMedium, color = muted)
            }
            if (clientName != null || project.estimateSeconds != null) Row(Modifier.padding(start = 19.dp), verticalAlignment = Alignment.CenterVertically) {
                Text(clientName ?: "No client", style = MaterialTheme.typography.bodyMedium, color = muted, modifier = Modifier.weight(1f))
                if (project.estimateSeconds != null) EstimateProgress(project.trackedSeconds, project.estimateSeconds)
            }
        }
        Icon(Icons.AutoMirrored.Filled.KeyboardArrowRight, null, Modifier.padding(start = 6.dp).size(20.dp), tint = MaterialTheme.colorScheme.outline)
    }
}

/** A project: what it is, the time on it, and its tasks. */
@Composable
private fun ProjectDetail(state: AppState, projectId: String, onBack: () -> Unit) {
    val app = LocalApp.current
    val project = state.project(projectId)
    var editing by remember { mutableStateOf(false) }
    var deleting by remember { mutableStateOf(false) }
    var newTask by remember { mutableStateOf("") }
    var renaming by remember { mutableStateOf<TaskResponse?>(null) }
    var deletingTask by remember { mutableStateOf<TaskResponse?>(null) }
    if (project == null) {
        SubPage("Project", onBack) { EmptyState(Icons.Outlined.Folder, "This project is gone") }
        return
    }
    fun saveTask(task: TaskResponse, name: String, done: Boolean) = app.launch {
        runCatching { app.repository.saveTask(project.id, name, task.estimateSeconds, done, task.id) }.onFailure { app.notices.show(it, "Couldn't change the task.") }
    }
    fun addTask() {
        val name = newTask.trim()
        if (name.isEmpty()) return
        newTask = ""
        app.launch { runCatching { app.repository.saveTask(project.id, name) }.onFailure { newTask = name; app.notices.show(it, "Couldn't add the task.") } }
    }

    SubPage(project.name, onBack, actions = {
        if (project.canManage) {
            var menu by remember { mutableStateOf(false) }
            Box {
                IconButton(onClick = { menu = true }) { Icon(Icons.Filled.MoreVert, "Project actions") }
                DropdownMenu(menu, { menu = false }) {
                    DropdownMenuItem({ Text("Edit") }, { menu = false; editing = true })
                    DropdownMenuItem({ Text(if (project.archivedAt == null) "Archive" else "Restore") }, {
                        menu = false
                        app.launch {
                            runCatching { app.repository.setProjectArchived(project.id, project.archivedAt == null) }
                                .onSuccess { app.notices.show(if (project.archivedAt == null) "“${project.name}” archived" else "“${project.name}” restored") }
                                .onFailure { app.notices.show(it, "Couldn't change the project.") }
                        }
                    })
                    DropdownMenuItem({ Text("Delete", color = MaterialTheme.colorScheme.error) }, { menu = false; deleting = true })
                }
            }
        }
    }) {
        LazyColumn(contentPadding = PaddingValues(top = 8.dp, bottom = 32.dp)) {
            item {
                Card {
                    Row(Modifier.fillMaxWidth().padding(16.dp), verticalAlignment = Alignment.CenterVertically) {
                        ProjectDot(project.color, 14.dp)
                        Spacer(Modifier.width(10.dp))
                        Text(project.name, style = MaterialTheme.typography.titleMedium, color = readableColor(project.color, Spindle.colors.dark) ?: Color.Unspecified)
                    }
                    RowDivider()
                    ValueRow("Client", state.client(project.clientId)?.name ?: "No client")
                    RowDivider()
                    ValueRow("Tracked", Durations.hoursMinutes(project.trackedSeconds))
                    if (project.estimateSeconds != null) { RowDivider(); ValueRow("Estimate", trailing = { EstimateProgress(project.trackedSeconds, project.estimateSeconds) }) }
                    RowDivider()
                    ValueRow("Billable", if (project.isBillable) "Yes" else "No")
                    if (project.isPrivate) { RowDivider(); ValueRow("Private", "Members and admins only") }
                    if (project.archivedAt != null) { RowDivider(); ValueRow("Status", "Archived") }
                }
            }
            item { SectionHeader("Tasks") }
            val count = project.tasks.size + if (project.canManage) 1 else 0
            cardItems(project.tasks, key = { it.id }) { index, task ->
                var menu by remember { mutableStateOf(false) }
                CardRow(index, count, onClick = if (project.canManage) ({ menu = true }) else null, inset = 50.dp) {
                    Row(Modifier.fillMaxWidth().heightIn(min = 50.dp).padding(horizontal = 16.dp, vertical = 10.dp), verticalAlignment = Alignment.CenterVertically) {
                        IconButton(onClick = { saveTask(task, task.name, !task.isDone) }, enabled = project.canManage, modifier = Modifier.size(26.dp)) {
                            Icon(if (task.isDone) Icons.Filled.CheckCircle else Icons.Outlined.Circle, if (task.isDone) "Done" else "Not done",
                                tint = if (task.isDone) Spindle.colors.brandText else MaterialTheme.colorScheme.onSurfaceVariant)
                        }
                        Spacer(Modifier.width(8.dp))
                        Text(task.name, Modifier.weight(1f), maxLines = 1, overflow = TextOverflow.Ellipsis,
                            color = if (task.isDone) MaterialTheme.colorScheme.onSurfaceVariant else MaterialTheme.colorScheme.onSurface,
                            textDecoration = if (task.isDone) TextDecoration.LineThrough else null)
                        if (task.estimateSeconds != null) EstimateProgress(task.trackedSeconds, task.estimateSeconds)
                        else Text(Durations.hoursMinutes(task.trackedSeconds), style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
                    }
                    DropdownMenu(menu, { menu = false }) {
                        DropdownMenuItem({ Text(if (task.isDone) "Not Done" else "Done") }, { menu = false; saveTask(task, task.name, !task.isDone) })
                        DropdownMenuItem({ Text("Rename") }, { menu = false; renaming = task })
                        DropdownMenuItem({ Text("Delete", color = MaterialTheme.colorScheme.error) }, { menu = false; deletingTask = task })
                    }
                }
            }
            if (project.canManage) item {
                CardRow(count - 1, count) {
                    Row(Modifier.fillMaxWidth().padding(start = 16.dp, end = 8.dp), verticalAlignment = Alignment.CenterVertically) {
                        PlainField(newTask, { newTask = it }, "Add a task", Modifier.weight(1f), KeyboardOptions(capitalization = KeyboardCapitalization.Sentences, imeAction = ImeAction.Done), ::addTask)
                        BarAction("Add", ::addTask, enabled = newTask.isNotBlank())
                    }
                }
            }
            if (project.tasks.isEmpty()) item { SectionFooter("Tasks split a project's time, e.g. design and development. Done tasks can't get new time.") }
        }
    }
    if (editing) ProjectEditor(state, project) { editing = false }
    if (deleting) DeleteProjectDialog(project, after = onBack) { deleting = false }
    renaming?.let { task ->
        NameDialog("Rename task", "Task name", task.name, "Rename", onConfirm = { saveTask(task, it, task.isDone) }, onDismiss = { renaming = null })
    }
    deletingTask?.let { task ->
        ConfirmDialog("Delete the task “${task.name}”?", "The time tracked on it stays on the project, without a task.", "Delete Task",
            onConfirm = { app.launch { runCatching { app.repository.deleteTask(project.id, task.id) }.onFailure { app.notices.show(it, "Couldn't delete the task.") } } },
            onDismiss = { deletingTask = null })
    }
}

/** Creates a project or changes one: its name, client, colour, estimate, rate, and whether it is billable and private. */
@Composable
private fun ProjectEditor(state: AppState, project: ProjectResponse?, onDismiss: () -> Unit) {
    val app = LocalApp.current
    val scope = rememberCoroutineScope()
    var name by remember { mutableStateOf(project?.name.orEmpty()) }
    var clientId by remember { mutableStateOf(project?.clientId) }
    var newClient by remember { mutableStateOf<String?>(null) }
    var color by remember { mutableStateOf(project?.color?.uppercase() ?: ProjectColors[state.projects.size % ProjectColors.size]) }
    var estimate by remember { mutableStateOf(project?.estimateSeconds?.let { hours(it / 3600.0) }.orEmpty()) }
    var rate by remember { mutableStateOf(project?.hourlyRate?.let { hours(it) }.orEmpty()) }
    var billable by remember { mutableStateOf(project?.isBillable ?: false) }
    var private by remember { mutableStateOf(project?.isPrivate ?: false) }
    var errors by remember { mutableStateOf(mapOf<String, String>()) }
    var formError by remember { mutableStateOf<String?>(null) }
    var saving by remember { mutableStateOf(false) }
    val currency = state.settings?.currency?.ifBlank { null } ?: "EUR"

    fun save() {
        val found = mutableMapOf<String, String>()
        if (name.isBlank()) found["name"] = "Enter a name."
        val estimateHours = estimate.trim().replace(',', '.').takeIf { it.isNotEmpty() }?.let { it.toDoubleOrNull()?.takeIf { h -> h > 0 } ?: run { found["estimateSeconds"] = "Enter the hours, e.g. 40."; null } }
        val rateAmount = rate.trim().replace(',', '.').takeIf { it.isNotEmpty() }?.let { it.toDoubleOrNull()?.takeIf { r -> r >= 0 } ?: run { found["hourlyRate"] = "Enter an amount, e.g. 80."; null } }
        if (newClient?.isBlank() == true) found["clientId"] = "Enter the new client's name."
        errors = found
        if (found.isNotEmpty()) return
        saving = true; formError = null
        scope.launch {
            runCatching {
                val client = newClient?.let { app.repository.saveClient(it.trim()).id } ?: clientId
                app.repository.saveProject(buildJsonObject {
                    put("name", name.trim()); put("clientId", client?.let(::JsonPrimitive) ?: JsonNull); put("color", color)
                    put("isPrivate", private); put("isBillable", billable)
                    put("estimateSeconds", estimateHours?.let { JsonPrimitive(Math.round(it * 3600)) } ?: JsonNull)
                    put("hourlyRate", rateAmount?.let(::JsonPrimitive) ?: JsonNull)
                }, project?.id)
            }.onSuccess { saved ->
                app.notices.show(if (project == null) "Project “${saved.name}” created" else "Project saved")
                onDismiss()
            }.onFailure { formError = it.message ?: "Couldn't save the project." }
            saving = false
        }
    }

    FullScreenEditor(if (project == null) "New Project" else "Edit Project", onDismiss, if (project == null) "Create" else "Save", ::save, !saving, "Cancel") {
        Column(Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(bottom = 32.dp)) {
            formError?.let { FormBanner(it, modifier = Modifier.padding(horizontal = 16.dp).padding(top = 12.dp)) }
            SectionHeader("Name")
            Card { PlainField(name, { name = it }, "Name", Modifier.padding(horizontal = 16.dp), KeyboardOptions(capitalization = KeyboardCapitalization.Sentences)) }
            errors["name"]?.let { SectionFooter(it, color = MaterialTheme.colorScheme.error) }
            Spacer(Modifier.height(20.dp))
            Card {
                val current = newClient
                if (current != null) Row(Modifier.padding(start = 16.dp, end = 8.dp), verticalAlignment = Alignment.CenterVertically) {
                    PlainField(current, { newClient = it }, "New client's name", Modifier.weight(1f), KeyboardOptions(capitalization = KeyboardCapitalization.Words))
                    BarAction("Cancel", { newClient = null })
                } else MenuRow(
                    "Client", clientId ?: "",
                    listOf("" to "No client") + state.clients.filter { it.archivedAt == null || it.id == clientId }.sortedBy { it.name.lowercase() }.map { it.id to it.name } +
                        (if (state.isAdmin) listOf("new" to "New Client…") else emptyList()),
                    { value -> if (value == "new") newClient = "" else clientId = value.ifEmpty { null } },
                )
            }
            errors["clientId"]?.let { SectionFooter(it, color = MaterialTheme.colorScheme.error) }
            SectionHeader("Color")
            Card {
                Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
                    ProjectColors.chunked(8).forEach { row ->
                        Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                            row.forEach { hex ->
                                val chosen = color.equals(hex, ignoreCase = true)
                                Box(Modifier.weight(1f).aspectRatio(1f).clip(CircleShape).background(projectColor(hex)).clickable { color = hex }, contentAlignment = Alignment.Center) {
                                    if (chosen) Icon(Icons.Filled.Check, "Chosen", Modifier.size(16.dp), tint = Color.White)
                                }
                            }
                            repeat(8 - row.size) { Spacer(Modifier.weight(1f)) }
                        }
                    }
                }
            }
            Spacer(Modifier.height(20.dp))
            Card {
                ValueRow("Estimate (hours)", trailing = {
                    PlainField(estimate, { estimate = it }, "Optional", Modifier.width(120.dp), KeyboardOptions(keyboardType = KeyboardType.Decimal), end = true)
                })
                RowDivider()
                ValueRow("Hourly rate ($currency)", trailing = {
                    PlainField(rate, { rate = it }, state.settings?.defaultHourlyRate?.let { "or ${ReportFormat.money(it, currency)}" } ?: "Member rates", Modifier.width(140.dp), KeyboardOptions(keyboardType = KeyboardType.Decimal), end = true)
                })
            }
            SectionFooter(errors["estimateSeconds"] ?: errors["hourlyRate"] ?: "Without a rate, time is billed at each member's rate.",
                color = if (errors.containsKey("estimateSeconds") || errors.containsKey("hourlyRate")) MaterialTheme.colorScheme.error else MaterialTheme.colorScheme.onSurfaceVariant)
            Spacer(Modifier.height(20.dp))
            Card {
                SwitchRow("Billable", billable, { billable = it })
                RowDivider()
                SwitchRow("Private", private, { private = it })
            }
            SectionFooter("New time on a billable project is billable. Only project members and admins see a private project.")
        }
    }
}

private fun hours(value: Double): String = if (value == Math.rint(value)) value.toLong().toString() else "%.2f".format(java.util.Locale.ROOT, value)

// MARK: - Clients

@Composable
private fun ClientList(state: AppState, choose: (ManagePart) -> Unit) {
    val app = LocalApp.current
    var showArchived by remember { mutableStateOf(false) }
    var naming by remember { mutableStateOf<Pair<ClientResponse?, Boolean>?>(null) }
    var deleting by remember { mutableStateOf<ClientResponse?>(null) }
    val clients = state.clients.filter { showArchived || it.archivedAt == null }.sortedBy { it.name.lowercase() }
    ManageScaffold(
        ManagePart.Clients, choose,
        navigation = {
            IconButton(onClick = { showArchived = !showArchived }) {
                Icon(if (showArchived) Icons.Filled.Inventory2 else Icons.Outlined.Inventory2, if (showArchived) "Hide archived clients" else "Show archived clients")
            }
        },
        actions = { if (state.isAdmin) IconButton(onClick = { naming = null to true }) { Icon(Icons.Filled.Add, "New client") } },
    ) {
        if (clients.isEmpty()) EmptyState(Icons.Outlined.Business, "No clients yet", if (state.isAdmin) "Tap + to add the first one." else "An admin can add clients.")
        else LazyColumn(contentPadding = PaddingValues(top = 8.dp, bottom = 24.dp)) {
            cardItems(clients, key = { it.id }) { index, client ->
                var menu by remember { mutableStateOf(false) }
                val count = state.projects.count { it.clientId == client.id && it.archivedAt == null }
                CardRow(index, clients.size, onClick = if (state.isAdmin) ({ menu = true }) else null) {
                    Row(Modifier.fillMaxWidth().heightIn(min = 50.dp).padding(horizontal = 16.dp), verticalAlignment = Alignment.CenterVertically) {
                        Text(client.name, maxLines = 1, overflow = TextOverflow.Ellipsis, modifier = Modifier.weight(1f, fill = false))
                        if (client.archivedAt != null) { Spacer(Modifier.width(8.dp)); Badge("Archived") }
                        Spacer(Modifier.weight(1f))
                        Text(if (count == 1) "1 project" else "$count projects", style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
                    }
                    DropdownMenu(menu, { menu = false }) {
                        DropdownMenuItem({ Text("Rename") }, { menu = false; naming = client to true })
                        DropdownMenuItem({ Text(if (client.archivedAt == null) "Archive" else "Restore") }, {
                            menu = false
                            app.launch { runCatching { app.repository.setClientArchived(client.id, client.archivedAt == null) }.onFailure { app.notices.show(it, "Couldn't change the client.") } }
                        })
                        DropdownMenuItem({ Text("Delete", color = MaterialTheme.colorScheme.error) }, { menu = false; deleting = client })
                    }
                }
            }
            item { SectionFooter("The people and companies you work for. Assign them to projects to see and bill their time together.") }
        }
    }
    naming?.let { (client, _) ->
        NameDialog(if (client == null) "New client" else "Rename client", "Client name", client?.name.orEmpty(), if (client == null) "Add" else "Rename",
            onConfirm = { name ->
                app.launch {
                    runCatching { app.repository.saveClient(name, client?.id) }
                        .onSuccess { app.notices.show(if (client == null) "Client “$name” added" else "Client renamed") }
                        .onFailure { app.notices.show(it, "Couldn't save the client.") }
                }
            }, onDismiss = { naming = null })
    }
    deleting?.let { client ->
        ConfirmDialog("Delete “${client.name}”?", "Its projects stay, without a client.", "Delete Client",
            onConfirm = { app.launch { runCatching { app.repository.deleteClient(client.id) }.onSuccess { app.notices.show("“${client.name}” deleted") }.onFailure { app.notices.show(it, "Couldn't delete the client.") } } },
            onDismiss = { deleting = null })
    }
}

// MARK: - Tags

@Composable
private fun TagList(state: AppState, choose: (ManagePart) -> Unit) {
    val app = LocalApp.current
    var naming by remember { mutableStateOf<Pair<TagResponse?, Boolean>?>(null) }
    var deleting by remember { mutableStateOf<TagResponse?>(null) }
    val tags = state.sortedTags
    ManageScaffold(ManagePart.Tags, choose, actions = { if (state.canCreateTags) IconButton(onClick = { naming = null to true }) { Icon(Icons.Filled.Add, "New tag") } }) {
        if (tags.isEmpty()) EmptyState(Icons.AutoMirrored.Outlined.Label, "No tags yet", if (state.canCreateTags) "Tap + to add the first one." else "An admin can add tags.")
        else LazyColumn(contentPadding = PaddingValues(top = 8.dp, bottom = 24.dp)) {
            cardItems(tags, key = { it.id }) { index, tag ->
                var menu by remember { mutableStateOf(false) }
                CardRow(index, tags.size, onClick = if (state.isAdmin) ({ menu = true }) else null, inset = 50.dp) {
                    Row(Modifier.fillMaxWidth().heightIn(min = 50.dp).padding(horizontal = 16.dp), verticalAlignment = Alignment.CenterVertically) {
                        Icon(Icons.AutoMirrored.Outlined.Label, null, Modifier.size(20.dp), tint = MaterialTheme.colorScheme.onSurfaceVariant)
                        Spacer(Modifier.width(14.dp))
                        Text(tag.name)
                    }
                    DropdownMenu(menu, { menu = false }) {
                        DropdownMenuItem({ Text("Rename") }, { menu = false; naming = tag to true })
                        DropdownMenuItem({ Text("Delete", color = MaterialTheme.colorScheme.error) }, { menu = false; deleting = tag })
                    }
                }
            }
            item { SectionFooter("Labels that cut across projects, such as meeting, review or overtime.") }
        }
    }
    naming?.let { (tag, _) ->
        NameDialog(if (tag == null) "New tag" else "Rename tag", "Tag name", tag?.name.orEmpty(), if (tag == null) "Add" else "Rename",
            onConfirm = { name ->
                app.launch {
                    runCatching { app.repository.saveTag(name, tag?.id) }
                        .onSuccess { app.notices.show(if (tag == null) "Tag “$name” added" else "Tag renamed") }
                        .onFailure { app.notices.show(it, "Couldn't save the tag.") }
                }
            }, onDismiss = { naming = null })
    }
    deleting?.let { tag ->
        ConfirmDialog("Delete the tag “${tag.name}”?", "It is removed from every time entry that has it. The time itself stays.", "Delete Tag",
            onConfirm = { app.launch { runCatching { app.repository.deleteTag(tag.id) }.onSuccess { app.notices.show("Tag “${tag.name}” deleted") }.onFailure { app.notices.show(it, "Couldn't delete the tag.") } } },
            onDismiss = { deleting = null })
    }
}
