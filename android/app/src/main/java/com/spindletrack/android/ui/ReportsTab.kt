package com.spindletrack.android.ui

import android.content.Context
import android.content.Intent
import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.LazyListScope
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.KeyboardArrowLeft
import androidx.compose.material.icons.automirrored.filled.KeyboardArrowRight
import androidx.compose.material.icons.filled.ArrowDownward
import androidx.compose.material.icons.filled.ArrowUpward
import androidx.compose.material.icons.filled.AttachMoney
import androidx.compose.material.icons.filled.CalendarMonth
import androidx.compose.material.icons.filled.Check
import androidx.compose.material.icons.filled.FilterList
import androidx.compose.material.icons.filled.MoreVert
import androidx.compose.material.icons.filled.Warning
import androidx.compose.material.icons.outlined.ViewAgenda
import androidx.compose.material3.*
import androidx.compose.material3.pulltorefresh.PullToRefreshBox
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.rotate
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.core.content.FileProvider
import com.spindletrack.android.data.*
import com.spindletrack.android.ui.theme.Spindle
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.serialization.json.*
import java.time.LocalDate
import java.time.ZoneOffset

private val reportTabs = listOf("summary" to "Summary", "detailed" to "Detailed", "weekly" to "Weekly")
private val reportPeriods = listOf("today" to "Today", "yesterday" to "Yesterday", "thisWeek" to "This week", "lastWeek" to "Last week", "thisMonth" to "This month", "lastMonth" to "Last month", "thisYear" to "This year", "lastYear" to "Last year")
private val groupings = listOf("project" to "Project", "client" to "Client", "user" to "Member", "tag" to "Tag", "task" to "Task", "description" to "Description")
private val sorts = listOf("date" to "Date", "duration" to "Duration", "description" to "Description", "project" to "Project", "user" to "Member")
private const val NoneFilter = "00000000-0000-0000-0000-000000000000"

/** Summary, detailed and weekly reports, with the same settings as the web and iPhone apps, so saved ones open alike. */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun ReportsTab(state: AppState) {
    val app = LocalApp.current
    val options = LocalLaunchOptions.current
    val context = LocalContext.current
    val time = rememberTime(state)
    val scope = rememberCoroutineScope()
    val prefs = remember { context.getSharedPreferences("reports", Context.MODE_PRIVATE) }
    val prefsKey = "settings:${state.workspaceId}"
    var settings by remember {
        val stored = prefs.getString(prefsKey, null)
        val initial = stored?.let { readReportSettings(it, "summary") } ?: buildJsonObject { put("tab", "summary"); put("period", "thisWeek") }
        mutableStateOf(options.report?.let { initial.with("tab", it) } ?: initial)
    }
    var savedReports by remember { mutableStateOf<List<SavedReportResponse>>(emptyList()) }
    var savedId by remember { mutableStateOf<String?>(null) }
    var members by remember { mutableStateOf<List<MemberResponse>>(emptyList()) }
    var groups by remember { mutableStateOf<List<GroupResponse>>(emptyList()) }
    var summary by remember { mutableStateOf<SummaryReport?>(null) }
    var detailed by remember { mutableStateOf<DetailedReport?>(null) }
    var weekly by remember { mutableStateOf<WeeklyReport?>(null) }
    var loading by remember { mutableStateOf(false) }
    var loadError by remember { mutableStateOf<String?>(null) }
    var revision by remember { mutableIntStateOf(0) }
    var filtering by remember { mutableStateOf(options.filters) }
    var choosingRange by remember { mutableStateOf(false) }
    var naming by remember { mutableStateOf(false) }
    var confirmingDelete by remember { mutableStateOf(false) }
    var exporting by remember { mutableStateOf(false) }
    var refreshing by remember { mutableStateOf(false) }

    val tab = settings.str("tab", "summary")
    val today = time.today()
    val range = runCatching {
        reportRange(settings.str("period", "thisWeek"), settings.str("from", today.toString()), settings.str("to", today.toString()), today, time.weekStart, tab == "weekly")
    }.getOrElse { reportRange("thisWeek", "", "", today, time.weekStart, tab == "weekly") }
    val current = savedReports.firstOrNull { it.id == savedId }
    val body = reportBody(settings, range)

    /** Changes the settings; anything that changes what the report covers starts it again from its first page. */
    fun change(edit: (JsonObject) -> JsonObject) { settings = edit(settings).with("page", null as JsonElement?) }
    fun shift(direction: Int) {
        val next = shiftRange(range, direction)
        change { it.with("period", "custom").with("from", next.first.toString()).with("to", next.second.toString()) }
    }

    LaunchedEffect(settings) { prefs.edit().putString(prefsKey, settings.with("page", null as JsonElement?).toString()).apply() }
    LaunchedEffect(state.workspaceId) {
        savedReports = runCatching { app.repository.savedReports() }.getOrDefault(emptyList())
        groups = runCatching { app.repository.groups() }.getOrDefault(emptyList())
        if (state.isAdmin) members = runCatching { app.repository.members() }.getOrDefault(emptyList())
    }
    LaunchedEffect(body.toString(), revision, state.entries) {
        // Typing a description in the filters loads once it pauses.
        delay(250)
        loading = true
        try {
            when (tab) {
                "detailed" -> detailed = app.repository.detailedReport(body)
                "weekly" -> weekly = app.repository.weeklyReport(body)
                else -> summary = app.repository.summaryReport(body)
            }
            loadError = null
        } catch (e: CancellationException) { throw e } catch (e: Exception) { loadError = e.message ?: "Couldn't load the report." }
        loading = false
    }

    fun export(format: String) {
        exporting = true
        scope.launch {
            runCatching { app.repository.exportReport(tab, format, body) }
                .onSuccess { share(context, it) }.onFailure { app.notices.show(it, "Couldn't export the report.") }
            exporting = false
        }
    }
    fun save(name: String, id: String?) = scope.launch {
        runCatching { app.repository.saveReport(name, tab, settings.with("page", null as JsonElement?).toString(), id) }
            .onSuccess { report ->
                savedReports = runCatching { app.repository.savedReports() }.getOrDefault(savedReports)
                savedId = report.id
                app.notices.show("“${report.name}” saved")
            }.onFailure { app.notices.show(it, "Couldn't save the report.") }
    }

    Scaffold(
        containerColor = MaterialTheme.colorScheme.background, contentWindowInsets = WindowInsets(0),
        topBar = {
            CenterBar(
                current?.name ?: "Reports",
                navigation = {
                    IconButton(onClick = { filtering = true }) { Icon(Icons.Filled.FilterList, if (settings.hasFilters) "Filters, some on" else "Filters") }
                },
                actions = {
                    var menu by remember { mutableStateOf(false) }
                    Box {
                        IconButton(onClick = { menu = true }, enabled = !exporting) {
                            if (exporting) CircularProgressIndicator(Modifier.size(20.dp), strokeWidth = 2.dp) else Icon(Icons.Filled.MoreVert, "Saved reports and export")
                        }
                        DropdownMenu(menu, { menu = false }) {
                            if (savedReports.isNotEmpty()) {
                                MenuHeading("Saved reports")
                                savedReports.forEach { report ->
                                    DropdownMenuItem({ Text(report.name) }, {
                                        menu = false
                                        settings = readReportSettings(report.parameters, report.kind)
                                        savedId = report.id
                                    }, trailingIcon = if (report.id == savedId) ({ Icon(Icons.Filled.Check, null, tint = Spindle.colors.brandText) }) else null)
                                }
                                HorizontalDivider()
                            }
                            current?.let { DropdownMenuItem({ Text("Save Changes to “${it.name}”") }, { menu = false; save(it.name, it.id) }) }
                            DropdownMenuItem({ Text("Save as New Report…") }, { menu = false; naming = true })
                            if (current != null) DropdownMenuItem({ Text("Delete Saved Report…", color = MaterialTheme.colorScheme.error) }, { menu = false; confirmingDelete = true })
                            HorizontalDivider()
                            MenuHeading("Export")
                            listOf("csv" to "CSV", "xlsx" to "Excel", "pdf" to "PDF").forEach { (format, label) ->
                                DropdownMenuItem({ Text(label) }, { menu = false; export(format) })
                            }
                        }
                    }
                },
            )
        },
    ) { padding ->
        PullToRefreshBox(refreshing, { scope.launch { refreshing = true; revision++; delay(400); refreshing = false } }, Modifier.padding(padding)) {
            LazyColumn(Modifier.fillMaxSize().alpha(if (loading) 0.6f else 1f), contentPadding = PaddingValues(bottom = 32.dp)) {
                item(key = "controls") {
                    Column(Modifier.padding(horizontal = 16.dp, vertical = 4.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
                        Segmented(reportTabs, tab) { value -> change { it.with("tab", value) } }
                        PeriodControl(rangeLabel(range), tab == "weekly", settings.str("period", "thisWeek"), { shift(it) }, { period ->
                            change { it.with("period", period).with("from", null as JsonElement?).with("to", null as JsonElement?) }
                        }, { choosingRange = true })
                        if (settings.hasFilters) Row(Modifier.padding(horizontal = 4.dp), verticalAlignment = Alignment.CenterVertically) {
                            Icon(Icons.Filled.FilterList, null, Modifier.size(16.dp), tint = MaterialTheme.colorScheme.onSurfaceVariant)
                            Spacer(Modifier.width(6.dp))
                            Text("Filtered", style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.weight(1f))
                            BrandLink("Clear Filters", { change { it.withoutFilters } }, style = MaterialTheme.typography.bodySmall)
                        }
                    }
                }
                val shown: Any? = when (tab) { "detailed" -> detailed; "weekly" -> weekly; else -> summary }
                if (loadError != null && shown == null) {
                    item {
                        Spacer(Modifier.height(12.dp))
                        Card {
                            EmptyState(Icons.Filled.Warning, "Couldn't load the report", loadError) { BrandLink("Try Again", { revision++ }) }
                        }
                    }
                } else when (tab) {
                    "detailed" -> detailed?.let { detailedContent(it, state, time, settings, ::change) } ?: loadingItem()
                    "weekly" -> weekly?.let { weeklyContent(it, state, time, settings, ::change) } ?: loadingItem()
                    else -> summary?.let { summaryContent(it, state, time, settings, ::change) } ?: loadingItem()
                }
            }
        }
    }

    if (filtering) ReportFiltersSheet(state, settings, members, groups, { change { _ -> it } }) { filtering = false }
    if (choosingRange) RangeDialog(range, tab == "weekly", { picked ->
        change { it.with("period", "custom").with("from", picked.first.toString()).with("to", picked.second.toString()) }
    }) { choosingRange = false }
    if (naming) NameDialog(
        "Save report", "Name", confirm = "Save", message = "Saved reports are shared with the workspace, on every device.",
        onConfirm = { save(it, null) }, onDismiss = { naming = false },
    )
    if (confirmingDelete && current != null) ConfirmDialog(
        "Delete “${current.name}”?", "The saved report goes for everyone who uses it; the time in it stays.", "Delete",
        onConfirm = {
            scope.launch {
                runCatching { app.repository.deleteSavedReport(current.id) }.onSuccess {
                    savedReports = savedReports.filterNot { it.id == current.id }; savedId = null
                    app.notices.show("“${current.name}” deleted")
                }.onFailure { app.notices.show(it, "Couldn't delete the report.") }
            }
        },
        onDismiss = { confirmingDelete = false },
    )
}

@Composable
private fun MenuHeading(text: String) {
    Text(text, style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.padding(horizontal = 12.dp, vertical = 6.dp))
}

private fun LazyListScope.loadingItem() = item {
    Spacer(Modifier.height(12.dp))
    Card { Box(Modifier.fillMaxWidth().padding(vertical = 30.dp), contentAlignment = Alignment.Center) { CircularProgressIndicator(Modifier.size(24.dp), strokeWidth = 2.5.dp) } }
}

/** Offers a file to share, save to Files or open in another app. */
private fun share(context: Context, file: ExportFile) {
    val uri = FileProvider.getUriForFile(context, context.packageName + ".files", file.file)
    val intent = Intent(Intent.ACTION_SEND).apply { type = file.mimeType; putExtra(Intent.EXTRA_STREAM, uri); addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION) }
    context.startActivity(Intent.createChooser(intent, file.fileName))
}

/** A segmented control, as the iPhone's: the choices side by side on a quiet track, the chosen one raised. */
@Composable
fun <T> Segmented(choices: List<Pair<T, String>>, selected: T, onSelect: (T) -> Unit) {
    Row(Modifier.fillMaxWidth().height(36.dp).clip(RoundedCornerShape(10.dp)).background(Spindle.colors.chip).padding(2.dp)) {
        choices.forEach { (value, label) ->
            val chosen = value == selected
            Box(
                Modifier.weight(1f).fillMaxHeight().clip(RoundedCornerShape(8.dp))
                    .background(if (chosen) MaterialTheme.colorScheme.surfaceContainerHigh else Color.Transparent)
                    .clickable { onSelect(value) },
                contentAlignment = Alignment.Center,
            ) {
                Text(label, style = MaterialTheme.typography.labelLarge.copy(fontWeight = if (chosen) FontWeight.SemiBold else FontWeight.Medium))
            }
        }
    }
}

/** The days the report covers, with the period before and after on either side and the presets in a menu. */
@Composable
private fun PeriodControl(label: String, weekly: Boolean, period: String, shift: (Int) -> Unit, choose: (String) -> Unit, custom: () -> Unit) {
    var menu by remember { mutableStateOf(false) }
    Row(Modifier.fillMaxWidth().height(40.dp).clip(CircleShape).background(MaterialTheme.colorScheme.surfaceContainerLow), verticalAlignment = Alignment.CenterVertically) {
        IconButton(onClick = { shift(-1) }) { Icon(Icons.AutoMirrored.Filled.KeyboardArrowLeft, if (weekly) "Previous week" else "Previous period", tint = Spindle.colors.brandText) }
        Box(Modifier.weight(1f)) {
            Row(Modifier.fillMaxWidth().clip(CircleShape).clickable { menu = true }.padding(vertical = 8.dp), horizontalArrangement = Arrangement.Center, verticalAlignment = Alignment.CenterVertically) {
                Icon(Icons.Filled.CalendarMonth, null, Modifier.size(18.dp), tint = Spindle.colors.brandText)
                Spacer(Modifier.width(8.dp))
                Text(label, style = MaterialTheme.typography.titleSmall.copy(fontWeight = FontWeight.SemiBold), color = Spindle.colors.brandText)
            }
            DropdownMenu(menu, { menu = false }) {
                val presets = if (weekly) reportPeriods.filter { it.first == "thisWeek" || it.first == "lastWeek" } else reportPeriods
                presets.forEach { (value, title) ->
                    DropdownMenuItem({ Text(title) }, { menu = false; choose(value) }, trailingIcon = if (value == period) ({ Icon(Icons.Filled.Check, null, tint = Spindle.colors.brandText) }) else null)
                }
                HorizontalDivider()
                DropdownMenuItem({ Text(if (weekly) "Choose a Week…" else "Custom Range…") }, { menu = false; custom() })
            }
        }
        IconButton(onClick = { shift(1) }) { Icon(Icons.AutoMirrored.Filled.KeyboardArrowRight, if (weekly) "Next week" else "Next period", tint = Spindle.colors.brandText) }
    }
}

/** Picks a range of days, or for the weekly report a week by any of its days. */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun RangeDialog(range: Pair<LocalDate, LocalDate>, weekly: Boolean, onPick: (Pair<LocalDate, LocalDate>) -> Unit, onDismiss: () -> Unit) {
    if (weekly) {
        DayPickerDialog(range.first, { onPick(it to it) }, onDismiss, title = "Week of")
        return
    }
    fun millis(day: LocalDate) = day.atStartOfDay(ZoneOffset.UTC).toInstant().toEpochMilli()
    fun day(millis: Long) = java.time.Instant.ofEpochMilli(millis).atZone(ZoneOffset.UTC).toLocalDate()
    val state = rememberDateRangePickerState(millis(range.first), millis(range.second))
    DatePickerDialog(
        onDismissRequest = onDismiss,
        confirmButton = {
            TextButton(onClick = {
                val from = state.selectedStartDateMillis?.let(::day)
                if (from != null) onPick(from to (state.selectedEndDateMillis?.let(::day) ?: from))
                onDismiss()
            }) { Text("Show", color = Spindle.colors.brandText) }
        },
        dismissButton = { TextButton(onClick = onDismiss) { Text("Cancel", color = Spindle.colors.brandText) } },
        colors = DatePickerDefaults.colors(containerColor = MaterialTheme.colorScheme.surfaceContainerHigh),
    ) {
        DateRangePicker(
            state, Modifier.weight(1f), title = { Text("Custom range", Modifier.padding(start = 24.dp, top = 16.dp)) },
            colors = DatePickerDefaults.colors(
                containerColor = MaterialTheme.colorScheme.surfaceContainerHigh,
                selectedDayContainerColor = Spindle.colors.brand, selectedDayContentColor = Spindle.colors.ink,
                dayInSelectionRangeContainerColor = Spindle.colors.brand.copy(alpha = 0.25f), todayDateBorderColor = Spindle.colors.brandText,
            ),
        )
    }
}

// MARK: - Totals

private data class Figure(val label: String, val value: String, val note: String)

/** The time and billable time (or billed time), and with rates the amount and what each tracked hour earned. */
private fun figures(totals: ReportTotals, currency: String, time: AccountTime): List<Figure> {
    val tracked = totals.seconds.toDouble()
    val list = mutableListOf(Figure("Total", time.duration(totals.seconds), if (totals.entryCount == 1) "1 entry" else "${totals.entryCount} entries"))
    val billed = totals.billedSeconds
    if (billed != null && billed != totals.billableSeconds) {
        val ratio = if (tracked > 0) "%.2f× the time tracked · ".format(billed / tracked) else ""
        list += Figure("Billed", (if (billed < 0) "−" else "") + time.duration(kotlin.math.abs(billed)), "$ratio${time.duration(totals.billableSeconds)} billable tracked")
    } else {
        list += Figure("Billable", time.duration(totals.billableSeconds), "${ReportFormat.percent(totals.billableSeconds.toDouble(), tracked)} of the total")
    }
    totals.amount?.let { amount ->
        list += Figure("Amount", ReportFormat.money(amount, currency), if (tracked > 0) "${ReportFormat.money(amount / (tracked / 3600), currency)}/h per hour tracked" else "Billed time at its rates")
        totals.laborCost?.takeIf { it > 0 }?.let { cost ->
            val profit = amount - cost
            list += Figure("Profit", ReportFormat.money(profit, currency), "${ReportFormat.money(cost, currency)} labor cost · ${ReportFormat.percent(maxOf(profit, 0.0), amount)} margin")
        }
    }
    return list
}

private fun LazyListScope.totals(totals: ReportTotals, currency: String, time: AccountTime) = item(key = "totals") {
    val list = figures(totals, currency, time)
    Column(Modifier.padding(horizontal = 16.dp).padding(top = 12.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
        list.chunked(2).forEach { pair ->
            Row(Modifier.height(IntrinsicSize.Min), horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                pair.forEach { figure ->
                    Column(Modifier.weight(1f).fillMaxHeight().clip(RoundedCornerShape(14.dp)).background(MaterialTheme.colorScheme.surfaceContainerLow).padding(12.dp), verticalArrangement = Arrangement.spacedBy(3.dp)) {
                        Text(figure.label, style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
                        Text(figure.value, style = MaterialTheme.typography.titleLarge.copy(fontFeatureSettings = "tnum"), maxLines = 1)
                        Text(figure.note, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                    }
                }
                if (pair.size == 1) Spacer(Modifier.weight(1f))
            }
        }
    }
}

// MARK: - Summary

private fun LazyListScope.summaryContent(report: SummaryReport, state: AppState, time: AccountTime, settings: JsonObject, change: ((JsonObject) -> JsonObject) -> Unit) {
    totals(report.totals, report.currency, time)
    item(key = "chart-header") { SectionHeader("Time by ${report.resolution.ifEmpty { "day" }}") }
    item(key = "chart") { Card { TimeChart(report.buckets, report.resolution, Modifier.padding(16.dp)) } }
    item(key = "breakdown-header") {
        val groupBy = settings.str("groupBy", "project")
        val subGroupBy = settings.str("subGroupBy", if (groupBy == "description") "none" else "description").takeIf { it != "none" && it != groupBy }
        SectionHeader("Breakdown") {
            GroupingMenu(groupBy, subGroupBy, onGroup = { value -> change { it.with("groupBy", value).let { s -> if (subGroupBy == value) s.with("subGroupBy", "none") else s } } }, onSubGroup = { value -> change { it.with("subGroupBy", value ?: "none") } })
        }
    }
    if (report.groups.isEmpty()) {
        item { Card { Text("No time tracked in this period${if (settings.hasFilters) " with these filters" else ""}.", color = MaterialTheme.colorScheme.onSurfaceVariant, textAlign = TextAlign.Center, modifier = Modifier.fillMaxWidth().padding(vertical = 24.dp, horizontal = 16.dp)) } }
    } else {
        item(key = "donut") {
            CardRow(0, report.groups.size + 1) {
                Donut(report.groups, report.totals.seconds, Modifier.fillMaxWidth().padding(vertical = 16.dp))
            }
        }
        cardItems(report.groups.mapIndexed { index, group -> index to group }, key = { "group-${it.second.key}" }) { index, (position, group) ->
            CardRow(index + 1, report.groups.size + 1) {
                GroupRow(group, ReportFormat.colorOf(group.key, group.color, position), report.totals, report.currency, time)
            }
        }
    }
}

@Composable
private fun GroupingMenu(groupBy: String, subGroupBy: String?, onGroup: (String) -> Unit, onSubGroup: (String?) -> Unit) {
    var open by remember { mutableStateOf(false) }
    Box {
        Row(Modifier.clip(CircleShape).clickable { open = true }.padding(horizontal = 6.dp, vertical = 2.dp), verticalAlignment = Alignment.CenterVertically) {
            Icon(Icons.Outlined.ViewAgenda, null, Modifier.size(15.dp), tint = Spindle.colors.brandText)
            Spacer(Modifier.width(5.dp))
            val label = groupings.first { it.first == groupBy }.second + (subGroupBy?.let { sub -> ", " + groupings.first { it.first == sub }.second } ?: "")
            Text(label, color = Spindle.colors.brandText, style = MaterialTheme.typography.labelLarge)
        }
        DropdownMenu(open, { open = false }) {
            MenuHeading("Group by")
            groupings.forEach { (value, title) ->
                DropdownMenuItem({ Text(title) }, { open = false; onGroup(value) }, trailingIcon = if (value == groupBy) ({ Icon(Icons.Filled.Check, null, tint = Spindle.colors.brandText) }) else null)
            }
            HorizontalDivider()
            MenuHeading("Then by")
            DropdownMenuItem({ Text("Nothing") }, { open = false; onSubGroup(null) }, trailingIcon = if (subGroupBy == null) ({ Icon(Icons.Filled.Check, null, tint = Spindle.colors.brandText) }) else null)
            groupings.filter { it.first != groupBy }.forEach { (value, title) ->
                DropdownMenuItem({ Text(title) }, { open = false; onSubGroup(value) }, trailingIcon = if (value == subGroupBy) ({ Icon(Icons.Filled.Check, null, tint = Spindle.colors.brandText) }) else null)
            }
        }
    }
}

@Composable
private fun GroupRow(group: SummaryGroup, color: String?, totals: ReportTotals, currency: String, time: AccountTime) {
    var open by remember { mutableStateOf(false) }
    val expandable = group.subGroups.isNotEmpty()
    val rotation by animateFloatAsState(if (open) 90f else 0f, label = "chevron")
    Column(Modifier.fillMaxWidth().clickable(enabled = expandable) { open = !open }.padding(horizontal = 16.dp, vertical = 12.dp)) {
        GroupLine(group, color, totals, currency, time, level = 0, trailing = {
            if (expandable) Icon(Icons.AutoMirrored.Filled.KeyboardArrowRight, if (open) "Collapse" else "Expand", Modifier.size(18.dp).rotate(rotation), tint = MaterialTheme.colorScheme.outline)
        })
        if (open) group.subGroups.forEach { sub ->
            Spacer(Modifier.height(10.dp))
            Box(Modifier.padding(start = 18.dp)) { GroupLine(sub, null, totals, currency, time, level = 1) }
        }
    }
}

@Composable
private fun GroupLine(group: SummaryGroup, color: String?, totals: ReportTotals, currency: String, time: AccountTime, level: Int, trailing: @Composable () -> Unit = {}) {
    val total = totals.seconds.toDouble()
    val style = if (level == 0) MaterialTheme.typography.bodyLarge else MaterialTheme.typography.bodyMedium
    Column(Modifier.fillMaxWidth(), verticalArrangement = Arrangement.spacedBy(6.dp)) {
        Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
            if (level == 0) {
                Box(Modifier.size(10.dp).clip(CircleShape).background(color?.let { projectColor(it) } ?: MaterialTheme.colorScheme.onSurfaceVariant.copy(alpha = 0.5f)))
                Spacer(Modifier.width(8.dp))
            }
            Row(Modifier.weight(1f), verticalAlignment = Alignment.CenterVertically) {
                Text(group.name, style = style, color = if (group.key == "none") MaterialTheme.colorScheme.onSurfaceVariant else MaterialTheme.colorScheme.onSurface, maxLines = 1, overflow = TextOverflow.Ellipsis, modifier = Modifier.weight(1f, fill = false))
                group.detail?.let { Text("  $it", style = style, color = MaterialTheme.colorScheme.onSurfaceVariant, maxLines = 1, overflow = TextOverflow.Ellipsis) }
            }
            Spacer(Modifier.width(8.dp))
            Text(time.duration(group.seconds), style = style.copy(fontWeight = if (level == 0) FontWeight.Medium else FontWeight.Normal))
            trailing()
        }
        Row(verticalAlignment = Alignment.CenterVertically) {
            val share = if (total > 0) (group.seconds / total).toFloat() else 0f
            val bar = MaterialTheme.colorScheme.onSurface.copy(alpha = if (level == 0) 0.55f else 0.25f)
            Canvas(Modifier.weight(1f).height(5.dp)) {
                drawRoundRect(bar, size = Size(size.width * share, size.height), cornerRadius = androidx.compose.ui.geometry.CornerRadius(size.height / 2))
            }
            Text(ReportFormat.percent(group.seconds.toDouble(), total), style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.onSurfaceVariant, textAlign = TextAlign.End, modifier = Modifier.width(48.dp))
            if (totals.amount != null) Text(ReportFormat.money(group.amount, currency), style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.onSurfaceVariant, textAlign = TextAlign.End, modifier = Modifier.padding(start = 8.dp))
        }
    }
}

/** Billable and non-billable hours per day, week or month, in neutral greys: the projects' colours carry the rest. */
@Composable
private fun TimeChart(buckets: List<ReportBucket>, resolution: String, modifier: Modifier = Modifier) {
    val ink = MaterialTheme.colorScheme.onSurface
    val muted = MaterialTheme.colorScheme.onSurfaceVariant
    val grid = Spindle.colors.hairline
    val ticks = ReportFormat.hourTicks((buckets.maxOfOrNull { it.seconds } ?: 0) / 3600.0)
    val top = ticks.last().coerceAtLeast(0.25)
    // Crowded axes label every few bars.
    val every = maxOf(1, buckets.size / 7)
    fun label(start: String): String {
        val day = runCatching { LocalDate.parse(start.take(10)) }.getOrNull() ?: return start
        fun f(pattern: String) = day.format(java.time.format.DateTimeFormatter.ofPattern(pattern, java.util.Locale.ENGLISH))
        return when (resolution) {
            "month" -> f("MMM")
            "week" -> f("d MMM")
            else -> if (buckets.size <= 7) f("EEE d") else f("d")
        }
    }
    Column(modifier) {
        Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.End, verticalAlignment = Alignment.CenterVertically) {
            Legend(ink.copy(alpha = 0.72f), "Billable"); Spacer(Modifier.width(12.dp)); Legend(ink.copy(alpha = 0.2f), "Non-billable")
        }
        Spacer(Modifier.height(8.dp))
        Row(Modifier.fillMaxWidth().height(170.dp)) {
            Column(Modifier.fillMaxHeight().padding(end = 6.dp), verticalArrangement = Arrangement.SpaceBetween, horizontalAlignment = Alignment.End) {
                ticks.reversed().forEach { Text(ReportFormat.hourTick(it), style = MaterialTheme.typography.labelSmall, color = muted) }
            }
            Canvas(Modifier.weight(1f).fillMaxHeight().padding(vertical = 7.dp)) {
                ticks.forEach { tick ->
                    val y = size.height * (1 - (tick / top)).toFloat()
                    drawLine(grid, Offset(0f, y), Offset(size.width, y), 1f)
                }
                if (buckets.isEmpty()) return@Canvas
                val slot = size.width / buckets.size
                val width = slot * 0.62f
                buckets.forEachIndexed { index, bucket ->
                    val x = slot * index + (slot - width) / 2
                    val all = (bucket.seconds / 3600.0 / top).toFloat() * size.height
                    val billable = (bucket.billableSeconds / 3600.0 / top).toFloat() * size.height
                    drawRect(ink.copy(alpha = 0.2f), Offset(x, size.height - all), Size(width, all - billable))
                    drawRect(ink.copy(alpha = 0.72f), Offset(x, size.height - billable), Size(width, billable))
                }
            }
        }
        Row(Modifier.fillMaxWidth().padding(start = 36.dp, top = 4.dp)) {
            buckets.forEachIndexed { index, bucket ->
                Text(if (index % every == 0) label(bucket.start) else "", style = MaterialTheme.typography.labelSmall, color = muted, textAlign = TextAlign.Center, maxLines = 1, modifier = Modifier.weight(1f))
            }
        }
    }
}

@Composable
private fun Legend(color: Color, label: String) {
    Box(Modifier.size(8.dp).clip(CircleShape).background(color))
    Spacer(Modifier.width(5.dp))
    Text(label, style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
}

/** Each group's share of the time, around the total. */
@Composable
private fun Donut(groups: List<SummaryGroup>, total: Long, modifier: Modifier = Modifier) {
    val fallback = MaterialTheme.colorScheme.onSurfaceVariant.copy(alpha = 0.5f)
    val colors = groups.mapIndexed { index, group -> ReportFormat.colorOf(group.key, group.color, index)?.let { projectColor(it) } ?: fallback }
    Box(modifier, contentAlignment = Alignment.Center) {
        Canvas(Modifier.size(180.dp)) {
            val stroke = size.minDimension * 0.19f
            val inset = stroke / 2
            var angle = -90f
            val sum = groups.sumOf { it.seconds }.coerceAtLeast(1)
            groups.forEachIndexed { index, group ->
                val sweep = 360f * group.seconds / sum
                drawArc(colors[index], angle + 0.6f, (sweep - 1.2f).coerceAtLeast(0.5f), false, Offset(inset, inset), Size(size.width - stroke, size.height - stroke), style = Stroke(stroke))
                angle += sweep
            }
        }
        Column(horizontalAlignment = Alignment.CenterHorizontally) {
            Text(Durations.hoursMinutes(total), style = MaterialTheme.typography.titleMedium)
            Text("total", style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
        }
    }
}

// MARK: - Detailed

private fun LazyListScope.detailedContent(report: DetailedReport, state: AppState, time: AccountTime, settings: JsonObject, change: ((JsonObject) -> JsonObject) -> Unit) {
    totals(report.totals, report.currency, time)
    item(key = "entries-header") {
        SectionHeader("${report.totalCount} ${if (report.totalCount == 1) "entry" else "entries"}") {
            SortMenu(settings.str("sort", "date"), !settings.bool("asc"), state.isAdmin,
                onSort = { value -> change { it.with("sort", value.takeIf { v -> v != "date" }).with("asc", if (value == "date" || value == "duration") null else JsonPrimitive(true)) } },
                onFlip = { descending -> change { it.with("asc", if (descending) JsonPrimitive(true) else null) } })
        }
    }
    if (report.entries.isEmpty()) item {
        Card { Text("No time entries in this period${if (settings.hasFilters) " with these filters" else ""}.", color = MaterialTheme.colorScheme.onSurfaceVariant, textAlign = TextAlign.Center, modifier = Modifier.fillMaxWidth().padding(24.dp)) }
    }
    cardItems(report.entries, key = { "${it.id}-${it.projectId}" }) { index, entry ->
        CardRow(index, report.entries.size) { DetailedRow(entry, state, time, report.currency, report.totals.amount != null) }
    }
    val pages = maxOf(1, (report.totalCount + report.pageSize - 1) / maxOf(1, report.pageSize))
    if (pages > 1) item(key = "pager") {
        val page = settings.num("page").toInt().coerceAtLeast(1)
        val first = (page - 1) * report.pageSize + 1
        val last = minOf(page * report.pageSize, report.totalCount)
        Row(Modifier.fillMaxWidth().padding(horizontal = 24.dp, vertical = 12.dp), verticalAlignment = Alignment.CenterVertically) {
            BrandLink("‹ Previous", { change { it.with("page", if (page - 1 > 1) JsonPrimitive(page - 1) else null) } }, enabled = page > 1)
            Text("$first–$last of ${report.totalCount}", Modifier.weight(1f), textAlign = TextAlign.Center, style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
            BrandLink("Next ›", { change { it.with("page", JsonPrimitive(page + 1)) } }, enabled = page < pages)
        }
    }
}

@Composable
private fun SortMenu(sort: String, descending: Boolean, admin: Boolean, onSort: (String) -> Unit, onFlip: (Boolean) -> Unit) {
    var open by remember { mutableStateOf(false) }
    Box {
        Row(Modifier.clip(CircleShape).clickable { open = true }.padding(horizontal = 6.dp, vertical = 2.dp), verticalAlignment = Alignment.CenterVertically) {
            Icon(if (descending) Icons.Filled.ArrowDownward else Icons.Filled.ArrowUpward, null, Modifier.size(15.dp), tint = Spindle.colors.brandText)
            Spacer(Modifier.width(4.dp))
            Text(sorts.firstOrNull { it.first == sort }?.second ?: "Date", color = Spindle.colors.brandText, style = MaterialTheme.typography.labelLarge)
        }
        DropdownMenu(open, { open = false }) {
            MenuHeading("Sort by")
            sorts.filter { admin || it.first != "user" }.forEach { (value, title) ->
                DropdownMenuItem({ Text(title) }, { open = false; onSort(value) }, trailingIcon = if (value == sort) ({ Icon(Icons.Filled.Check, null, tint = Spindle.colors.brandText) }) else null)
            }
            HorizontalDivider()
            DropdownMenuItem({ Text(if (descending) "Ascending" else "Descending") }, { open = false; onFlip(descending) })
        }
    }
}

@Composable
private fun DetailedRow(entry: DetailedEntry, state: AppState, time: AccountTime, currency: String, showMoney: Boolean) {
    val muted = MaterialTheme.colorScheme.onSurfaceVariant
    Column(Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 12.dp), verticalArrangement = Arrangement.spacedBy(4.dp)) {
        Row(verticalAlignment = Alignment.Top) {
            Text(entry.description.ifEmpty { "No description" }, Modifier.weight(1f).padding(end = 8.dp), color = if (entry.description.isEmpty()) muted else MaterialTheme.colorScheme.onSurface, maxLines = 2, overflow = TextOverflow.Ellipsis)
            Column(horizontalAlignment = Alignment.End) {
                Text(time.duration(entry.seconds), fontWeight = FontWeight.Medium)
                if (showMoney && entry.amount != null) Text(ReportFormat.money(entry.amount, currency), style = MaterialTheme.typography.labelMedium, color = muted)
            }
        }
        Row(verticalAlignment = Alignment.CenterVertically) {
            if (entry.projectName != null) {
                ProjectDot(entry.projectColor)
                Spacer(Modifier.width(6.dp))
                Text(entry.projectName, style = MaterialTheme.typography.bodyMedium, color = readableColor(entry.projectColor, Spindle.colors.dark) ?: muted, maxLines = 1, fontWeight = FontWeight.Medium)
                listOfNotNull(entry.taskName, entry.clientName).firstOrNull()?.let { Text(" · $it", style = MaterialTheme.typography.bodyMedium, color = muted, maxLines = 1, overflow = TextOverflow.Ellipsis) }
            } else Text("No project", style = MaterialTheme.typography.bodyMedium, color = muted)
            if (entry.percent in 1..99) Text("  ${entry.percent}% of a split", style = MaterialTheme.typography.bodyMedium, color = muted, maxLines = 1)
        }
        FlowRow(horizontalArrangement = Arrangement.spacedBy(6.dp), verticalArrangement = Arrangement.spacedBy(4.dp), itemVerticalAlignment = Alignment.CenterVertically) {
            val start = runCatching { java.time.Instant.parse(entry.start) }.getOrNull()
            val stop = runCatching { java.time.Instant.parse(entry.stop) }.getOrNull()
            if (start != null) Text("${time.longDay(time.day(start))} · ${time.clock(start)} – ${stop?.let(time::clock) ?: ""}", style = MaterialTheme.typography.bodySmall, color = muted)
            if (state.isAdmin) Text(entry.userName, style = MaterialTheme.typography.bodySmall, color = muted)
            if (entry.isBillable) Icon(Icons.Filled.AttachMoney, "Billable", Modifier.size(14.dp), tint = muted)
            entry.tags.forEach { TagChip(it) }
        }
    }
}

// MARK: - Weekly

private fun LazyListScope.weeklyContent(report: WeeklyReport, state: AppState, time: AccountTime, settings: JsonObject, change: ((JsonObject) -> JsonObject) -> Unit) {
    totals(report.totals, report.currency, time)
    val rowsBy = settings.str("groupBy", "project").let { if (it == "description") "project" else it }
    item(key = "weekly-header") {
        SectionHeader("By day") {
            var open by remember { mutableStateOf(false) }
            Box {
                Row(Modifier.clip(CircleShape).clickable { open = true }.padding(horizontal = 6.dp, vertical = 2.dp), verticalAlignment = Alignment.CenterVertically) {
                    Icon(Icons.Outlined.ViewAgenda, null, Modifier.size(15.dp), tint = Spindle.colors.brandText)
                    Spacer(Modifier.width(5.dp))
                    Text(groupings.first { it.first == rowsBy }.second, color = Spindle.colors.brandText, style = MaterialTheme.typography.labelLarge)
                }
                DropdownMenu(open, { open = false }) {
                    groupings.filter { it.first != "description" }.forEach { (value, title) ->
                        DropdownMenuItem({ Text(title) }, { open = false; change { it.with("groupBy", value) } }, trailingIcon = if (value == rowsBy) ({ Icon(Icons.Filled.Check, null, tint = Spindle.colors.brandText) }) else null)
                    }
                }
            }
        }
    }
    item(key = "weekly") {
        Card {
            if (report.rows.isEmpty()) {
                Text("No time tracked this week${if (settings.hasFilters) " with these filters" else ""}.", color = MaterialTheme.colorScheme.onSurfaceVariant, textAlign = TextAlign.Center, modifier = Modifier.fillMaxWidth().padding(24.dp))
            } else {
                // Sideways, when the week is wider than the phone.
                Column(Modifier.horizontalScroll(rememberScrollState()).padding(horizontal = 16.dp, vertical = 10.dp)) {
                    val showMoney = report.totals.amount != null
                    val muted = MaterialTheme.colorScheme.onSurfaceVariant
                    val cell = Modifier.width(62.dp)
                    Row(Modifier.padding(bottom = 6.dp)) {
                        Text(groupings.first { it.first == rowsBy }.second, Modifier.width(140.dp), style = MaterialTheme.typography.labelMedium, color = muted)
                        report.days.forEach { day ->
                            Text(runCatching { LocalDate.parse(day).format(java.time.format.DateTimeFormatter.ofPattern("EEE d", java.util.Locale.ENGLISH)) }.getOrDefault(day), cell, style = MaterialTheme.typography.labelMedium, color = muted, textAlign = TextAlign.End)
                        }
                        Text("Total", cell, style = MaterialTheme.typography.labelMedium, color = muted, textAlign = TextAlign.End)
                        if (showMoney) Text("Amount", Modifier.width(84.dp), style = MaterialTheme.typography.labelMedium, color = muted, textAlign = TextAlign.End)
                    }
                    report.rows.forEachIndexed { index, row ->
                        HorizontalDivider(thickness = 0.5.dp, color = Spindle.colors.hairline)
                        Row(Modifier.padding(vertical = 9.dp), verticalAlignment = Alignment.CenterVertically) {
                            Row(Modifier.width(140.dp), verticalAlignment = Alignment.CenterVertically) {
                                Box(Modifier.size(8.dp).clip(CircleShape).background(ReportFormat.colorOf(row.key, row.color, index)?.let { projectColor(it) } ?: muted.copy(alpha = 0.5f)))
                                Spacer(Modifier.width(6.dp))
                                Text(row.name, style = MaterialTheme.typography.bodyMedium, maxLines = 1, overflow = TextOverflow.Ellipsis, color = if (row.key == "none") muted else MaterialTheme.colorScheme.onSurface)
                            }
                            row.days.forEach { WeekCell(it, time, cell) }
                            Text(time.duration(row.seconds), cell, style = MaterialTheme.typography.bodyMedium.copy(fontWeight = FontWeight.SemiBold), textAlign = TextAlign.End)
                            if (showMoney) Text(ReportFormat.money(row.amount, report.currency), Modifier.width(84.dp), style = MaterialTheme.typography.bodyMedium, textAlign = TextAlign.End)
                        }
                    }
                    HorizontalDivider(thickness = 0.5.dp, color = Spindle.colors.hairline)
                    Row(Modifier.padding(top = 9.dp)) {
                        Text("Total", Modifier.width(140.dp), style = MaterialTheme.typography.bodyMedium.copy(fontWeight = FontWeight.SemiBold))
                        report.dayTotals.forEach { WeekCell(it, time, cell, bold = true) }
                        Text(time.duration(report.totals.seconds), cell, style = MaterialTheme.typography.bodyMedium.copy(fontWeight = FontWeight.Bold), textAlign = TextAlign.End)
                        if (showMoney) Text(ReportFormat.money(report.totals.amount, report.currency), Modifier.width(84.dp), style = MaterialTheme.typography.bodyMedium.copy(fontWeight = FontWeight.SemiBold), textAlign = TextAlign.End)
                    }
                }
            }
        }
    }
}

@Composable
private fun WeekCell(seconds: Long, time: AccountTime, modifier: Modifier, bold: Boolean = false) {
    Text(
        if (seconds > 0) time.duration(seconds) else "–", modifier,
        style = MaterialTheme.typography.bodyMedium.copy(fontWeight = if (bold) FontWeight.SemiBold else FontWeight.Normal),
        color = if (seconds > 0) MaterialTheme.colorScheme.onSurface else MaterialTheme.colorScheme.onSurfaceVariant, textAlign = TextAlign.End,
    )
}

// MARK: - Filters

private data class FilterOption(val id: String, val label: String, val color: String? = null, val detail: String? = null)

/** What a report is narrowed to, and how its durations are rounded. Changes apply as they are made. */
@Composable
private fun ReportFiltersSheet(state: AppState, settings: JsonObject, members: List<MemberResponse>, groups: List<GroupResponse>, onChange: (JsonObject) -> Unit, onDismiss: () -> Unit) {
    var page by remember { mutableStateOf<String?>(null) }
    var description by remember { mutableStateOf(settings.str("q")) }
    LaunchedEffect(description) {
        val text = description.trim()
        if (text != settings.str("q")) onChange(settings.with("q", text.takeIf { it.isNotEmpty() }?.take(200)))
    }
    val choices: Map<String, Pair<String, List<Pair<String?, List<FilterOption>>>>> = mapOf(
        "members" to ("Members" to listOfNotNull(
            groups.takeIf { it.isNotEmpty() }?.let { list -> "Groups" to list.map { FilterOption(it.id, it.name, detail = "${it.memberIds.size} members") } },
            "People" to members.sortedBy { it.fullName.lowercase() }.map { FilterOption(it.userId, it.fullName.ifBlank { it.email }) },
        )),
        "projects" to ("Projects" to listOf(null to listOf(FilterOption(NoneFilter, "No project"))) + state.projectGroups().map { (client, projects) ->
            (client?.name ?: "No client") to projects.map { FilterOption(it.id, it.name, it.color) }
        }),
        "clients" to ("Clients" to listOf(null to (listOf(FilterOption(NoneFilter, "No client")) + state.clients.sortedBy { it.name.lowercase() }.map { FilterOption(it.id, it.name) }))),
        "tags" to ("Tags" to listOf(null to (listOf(FilterOption(NoneFilter, "No tag")) + state.sortedTags.map { FilterOption(it.id, it.name) }))),
    )
    fun selected(key: String) = if (key == "members") settings.ids("groups") + settings.ids("users") else settings.ids(key)
    fun set(key: String, ids: List<String>) {
        fun array(list: List<String>) = list.takeIf { it.isNotEmpty() }?.let { JsonArray(it.map(::JsonPrimitive)) }
        if (key == "members") {
            val groupIds = groups.map { it.id }.toSet()
            onChange(settings.with("groups", array(ids.filter { it in groupIds })).with("users", array(ids.filterNot { it in groupIds })))
        } else onChange(settings.with(key, array(ids)))
    }

    PickerSheet(page?.let { choices[it]!!.first } ?: "Filters", onDismiss, skipPartial = true, actions = {
        if (page != null && selected(page!!).isNotEmpty()) BarAction("Clear", { set(page!!, emptyList()) })
        BarAction("Done", { if (page != null) page = null else onDismiss() }, prominent = true)
    }) {
        val shownPage = page
        if (shownPage != null) {
            androidx.activity.compose.BackHandler { page = null }
            FilterChoiceList(choices[shownPage]!!.second, selected(shownPage)) { set(shownPage, it) }
        } else LazyColumn(Modifier.fillMaxHeight(0.9f), contentPadding = PaddingValues(top = 8.dp, bottom = 32.dp)) {
            item {
                Card {
                    val keys = listOfNotNull("members".takeIf { state.isAdmin }, "projects", "clients", "tags")
                    keys.forEachIndexed { index, key ->
                        if (index > 0) RowDivider()
                        val count = selected(key).size
                        ValueRow(choices[key]!!.first, if (count == 0) "All" else "$count chosen", onClick = { page = key })
                    }
                    RowDivider()
                    MenuRow("Billable", settings.str("billable"), listOf("" to "All time", "yes" to "Billable", "no" to "Non-billable"), { value -> onChange(settings.with("billable", value.takeIf { it.isNotEmpty() })) })
                }
            }
            item { SectionHeader("Description contains") }
            item { Card { PlainField(description, { description = it }, "Any description", Modifier.padding(horizontal = 16.dp)) } }
            item { Spacer(Modifier.height(20.dp)) }
            item {
                Card {
                    val rounding = settings.str("rounding", "none")
                    MenuRow("Rounding", rounding, listOf("none" to "None", "up" to "Up", "nearest" to "To nearest", "down" to "Down"), { value -> onChange(settings.with("rounding", value.takeIf { it != "none" })) })
                    RowDivider()
                    MenuRow("To", settings.num("roundTo").toInt().takeIf { it > 0 } ?: 15, listOf(1, 5, 6, 10, 12, 15, 30, 60).map { it to if (it == 60) "1 hour" else "$it minutes" },
                        { value -> onChange(settings.with("roundTo", if (value == 15) null else JsonPrimitive(value))) }, enabled = rounding != "none")
                }
            }
            item { SectionFooter("Rounds each entry's duration in the report; the entries themselves stay as they are.") }
            if (settings.hasFilters) {
                item { Spacer(Modifier.height(20.dp)) }
                item { Card { ActionRow("Clear Filters", { description = ""; onChange(settings.withoutFilters) }, destructive = true) } }
            }
        }
    }
}

/** Ticks any number of a filter's options, e.g. projects; searching narrows the list. */
@Composable
private fun FilterChoiceList(sections: List<Pair<String?, List<FilterOption>>>, selected: List<String>, onChange: (List<String>) -> Unit) {
    var chosen by remember { mutableStateOf(selected) }
    var search by remember { mutableStateOf("") }
    val term = search.trim()
    Column(Modifier.fillMaxHeight(0.9f)) {
        SearchField(search, { search = it }, "Search")
        LazyColumn(contentPadding = PaddingValues(bottom = 32.dp)) {
            sections.forEach { (heading, options) ->
                val shown = options.filter { term.isEmpty() || it.label.contains(term, true) }
                if (shown.isEmpty()) return@forEach
                item { if (heading != null) SectionHeader(heading) else Spacer(Modifier.height(12.dp)) }
                cardItems(shown, key = { "${heading}-${it.id}" }) { index, option ->
                    CardRow(index, shown.size, onClick = {
                        chosen = if (option.id in chosen) chosen - option.id else chosen + option.id
                        onChange(chosen)
                    }) {
                        Row(Modifier.fillMaxWidth().heightIn(min = 50.dp).padding(horizontal = 16.dp), verticalAlignment = Alignment.CenterVertically) {
                            option.color?.let { ProjectDot(it, 10.dp); Spacer(Modifier.width(10.dp)) }
                            Text(option.label, maxLines = 1, overflow = TextOverflow.Ellipsis, color = if (option.id == NoneFilter) MaterialTheme.colorScheme.onSurfaceVariant else MaterialTheme.colorScheme.onSurface)
                            option.detail?.let { Text("  $it", color = MaterialTheme.colorScheme.onSurfaceVariant, maxLines = 1) }
                            Spacer(Modifier.weight(1f))
                            if (option.id in chosen) Icon(Icons.Filled.Check, "Chosen", Modifier.size(20.dp), tint = Spindle.colors.brandText)
                        }
                    }
                }
            }
        }
    }
}

