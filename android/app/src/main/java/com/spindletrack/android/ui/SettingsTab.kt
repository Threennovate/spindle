package com.spindletrack.android.ui

import android.content.Intent
import android.net.Uri
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.KeyboardArrowRight
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.Remove
import androidx.compose.material.icons.filled.Check
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.input.nestedscroll.nestedScroll
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.KeyboardCapitalization
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.core.content.FileProvider
import androidx.lifecycle.compose.LifecycleResumeEffect
import com.spindletrack.android.BuildConfig
import com.spindletrack.android.TimerNotifications
import com.spindletrack.android.data.*
import com.spindletrack.android.ui.theme.Spindle
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import java.time.Duration
import java.time.Instant
import java.time.ZoneId

private enum class SettingsPage { Profile, TimeZone, Plan, Password, Sessions, Delete }

/**
 * The account and the workspace: who you are, how times are shown, the Pomodoro, security, your data, and signing out.
 * Preferences are the account's, the same on every device, and save as they change.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun SettingsTab(state: AppState) {
    val app = LocalApp.current
    val context = LocalContext.current
    val scroll = TopAppBarDefaults.exitUntilCollapsedScrollBehavior()
    var page by remember { mutableStateOf<SettingsPage?>(null) }
    var naming by remember { mutableStateOf(false) }
    var exporting by remember { mutableStateOf(false) }
    var sendingLink by remember { mutableStateOf(false) }
    val me = state.me ?: return
    // An account made with Google or Apple has no password to change; the reset email sets its first one.
    val passwordless = me.hasPassword == false

    when (page) {
        SettingsPage.Profile, SettingsPage.TimeZone -> { ProfilePage(me, page == SettingsPage.TimeZone, { page = it }) { page = null }; return }
        SettingsPage.Plan -> { state.planOrganization?.let { PlanPage(state, it) { page = null }; return } ?: run { page = null } }
        SettingsPage.Password -> { PasswordPage { page = null }; return }
        SettingsPage.Sessions -> { SessionsPage { page = null }; return }
        SettingsPage.Delete -> { DeleteAccountPage(passwordless) { page = null }; return }
        null -> {}
    }

    Scaffold(Modifier.nestedScroll(scroll.nestedScrollConnection), containerColor = MaterialTheme.colorScheme.background, contentWindowInsets = WindowInsets(0), topBar = { LargeBar("Settings", scroll) }) { padding ->
        LazyColumn(Modifier.padding(padding), contentPadding = PaddingValues(top = 4.dp, bottom = 32.dp)) {
            item {
                Card {
                    Row(Modifier.fillMaxWidth().combinedClickableCompat({ page = SettingsPage.Profile }, null).padding(16.dp), verticalAlignment = Alignment.CenterVertically) {
                        Avatar(me.fullName, 48.dp)
                        Spacer(Modifier.width(14.dp))
                        Column(Modifier.weight(1f)) {
                            Text(me.fullName, style = MaterialTheme.typography.titleMedium)
                            Text(me.email, style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
                        }
                        Icon(Icons.AutoMirrored.Filled.KeyboardArrowRight, null, tint = MaterialTheme.colorScheme.outline)
                    }
                    if (!me.emailConfirmed) {
                        RowDivider()
                        ActionRow("Send Confirmation Email", {
                            app.launch { runCatching { app.repository.sendConfirmationEmail() }.onSuccess { app.notices.show("Confirmation email sent to ${me.email}") }.onFailure { app.notices.show(it, "Couldn't send the email.") } }
                        })
                    }
                }
            }
            item { Spacer(Modifier.height(20.dp)) }
            item {
                Card {
                    MenuRow("Workspace", state.workspaceId, me.workspaces.map { it.id to it.name }, { id ->
                        if (id != null && id != state.workspaceId) app.launch { runCatching { app.repository.switchWorkspace(id) }.onFailure { app.notices.show(it, "Couldn't switch workspaces.") } }
                    })
                    if (state.canCreateWorkspaces) { RowDivider(); ActionRow("New Workspace…", { naming = true }) }
                }
            }
            item { SectionFooter("Projects, clients and the time you track belong to a workspace.") }
            state.planOrganization?.let { organization ->
                item { Spacer(Modifier.height(20.dp)) }
                item { Card { ValueRow("Plan", organization.planSummary(), onClick = { page = SettingsPage.Plan }) } }
            }
            item { PreferencesSections(me) }
            item { LiveUpdatesSection() }
            item { SectionHeader("Security") }
            item {
                Card {
                    if (passwordless) ActionRow("Email Me a Link to Set a Password", {
                        sendingLink = true
                        app.launch {
                            runCatching { app.repository.forgotPassword(me.email) }.onSuccess { app.notices.show("We sent a link to ${me.email}") }.onFailure { app.notices.show(it, "Couldn't send the email.") }
                            sendingLink = false
                        }
                    }, enabled = !sendingLink)
                    else ValueRow("Change Password", onClick = { page = SettingsPage.Password })
                    RowDivider()
                    ValueRow("Where You're Signed In", onClick = { page = SettingsPage.Sessions })
                }
            }
            if (passwordless) item { SectionFooter("You sign in with Google or Apple, so your account has no password. Add one to also sign in with your email address.") }
            item { SectionHeader("Your data") }
            item {
                Card {
                    ActionRow("Export My Data", {
                        exporting = true
                        app.launch {
                            runCatching { app.repository.exportData() }.onSuccess { file ->
                                val uri = FileProvider.getUriForFile(context, context.packageName + ".files", file.file)
                                context.startActivity(Intent.createChooser(Intent(Intent.ACTION_SEND).apply { type = file.mimeType; putExtra(Intent.EXTRA_STREAM, uri); addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION) }, file.fileName))
                            }.onFailure { app.notices.show(it, "Couldn't export your data.") }
                            exporting = false
                        }
                    }, enabled = !exporting, trailing = { if (exporting) CircularProgressIndicator(Modifier.size(18.dp), strokeWidth = 2.dp) })
                    RowDivider()
                    ValueRow("Delete Account", onClick = { page = SettingsPage.Delete }, labelColor = MaterialTheme.colorScheme.error)
                }
            }
            item { SectionFooter("A copy of your profile and every time entry you tracked, in every workspace, as a JSON file.") }
            item { Spacer(Modifier.height(20.dp)) }
            item {
                Card {
                    ActionRow("Open Spindle on the Web", { context.startActivity(Intent(Intent.ACTION_VIEW, Uri.parse(app.repository.serverUrl))) })
                    RowDivider()
                    ActionRow("Sign Out", { app.requestSignOut() }, destructive = true)
                }
            }
            item {
                Text("Spindle ${BuildConfig.VERSION_NAME}", style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant, textAlign = TextAlign.Center, modifier = Modifier.fillMaxWidth().padding(top = 16.dp))
            }
        }
    }
    if (naming) NameDialog(
        "New workspace", "Workspace name", confirm = "Create", message = "A workspace of its own, in the same organization, with its own projects, clients and tags.",
        onConfirm = { name -> app.launch { runCatching { app.repository.createWorkspace(name) }.onSuccess { app.notices.show("Workspace “$name” created") }.onFailure { app.notices.show(it, "Couldn't create the workspace.") } } },
        onDismiss = { naming = false },
    )
}

/** How dates, times and durations are shown, and the Pomodoro, saved a moment after they change. */
@Composable
private fun PreferencesSections(me: MeResponse) {
    val app = LocalApp.current
    var values by remember(me.preferences) { mutableStateOf(me.preferences) }
    LaunchedEffect(values) {
        if (values == me.preferences) return@LaunchedEffect
        delay(600)
        runCatching { app.repository.updatePreferences(values) }.onFailure { app.notices.show(it, "Couldn't save your preferences.") }
    }
    val weekdays = listOf("Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday")
    val sample = java.time.LocalDate.of(2026, 9, 28)
    Column {
        SectionHeader("Preferences")
        Card {
            MenuRow("First day of the week", values.weekStart, weekdays.mapIndexed { index, day -> index to day }, { values = values.copy(weekStart = it) })
            RowDivider()
            MenuRow("Date format", values.dateFormat, listOf("d MMM yyyy", "dd.MM.yyyy", "dd/MM/yyyy", "MM/dd/yyyy", "yyyy-MM-dd").map { it to sample.format(java.time.format.DateTimeFormatter.ofPattern(it, java.util.Locale.ENGLISH)) }, { values = values.copy(dateFormat = it) })
            RowDivider()
            MenuRow("Time format", values.timeFormat, listOf("24h" to "24-hour (13:30)", "12h" to "12-hour (1:30 pm)"), { values = values.copy(timeFormat = it) })
            RowDivider()
            MenuRow("Duration format", values.durationFormat, listOf("hms" to "1:30:00", "hm" to "1:30", "decimal" to "1.50 h"), { values = values.copy(durationFormat = it) })
        }
        SectionFooter("How dates, times and durations are shown to you, on every device.")
        SectionHeader("Pomodoro")
        Card {
            SwitchRow("Use the Pomodoro", values.pomodoroEnabled, { values = values.copy(pomodoroEnabled = it) })
            if (values.pomodoroEnabled) {
                RowDivider()
                StepperRow("Focus: ${values.pomodoroMinutes} minutes", values.pomodoroMinutes, 5..180, 5) { values = values.copy(pomodoroMinutes = it) }
                RowDivider()
                StepperRow("Break: ${values.pomodoroBreakMinutes} minutes", values.pomodoroBreakMinutes, 1..60, 1) { values = values.copy(pomodoroBreakMinutes = it) }
                RowDivider()
                SwitchRow("Stop the timer when a focus interval ends", values.pomodoroAutoStop, { values = values.copy(pomodoroAutoStop = it) })
            }
        }
        SectionFooter("Work in focused intervals with short breaks; Spindle tells you when each ends.")
    }
}

/** Only when Live Updates are off for Spindle on Android 16: the way back to the running timer on the lock screen. */
@Composable
private fun LiveUpdatesSection() {
    val context = LocalContext.current
    var off by remember { mutableStateOf(TimerNotifications.liveUpdatesOff(context)) }
    LifecycleResumeEffect(Unit) { off = TimerNotifications.liveUpdatesOff(context); onPauseOrDispose {} }
    if (!off) return
    Column {
        SectionHeader("Lock Screen")
        Card { ActionRow("Turn On Live Updates", { context.startActivity(TimerNotifications.liveUpdatesSettings(context)) }) }
        SectionFooter("Shows the running timer on the lock screen and in the status bar.")
    }
}

/** A number changed a step at a time, as the iPhone's stepper. */
@Composable
private fun StepperRow(label: String, value: Int, range: IntRange, step: Int, onChange: (Int) -> Unit) {
    Row(Modifier.fillMaxWidth().heightIn(min = 52.dp).padding(start = 16.dp, end = 8.dp), verticalAlignment = Alignment.CenterVertically) {
        Text(label, style = MaterialTheme.typography.bodyLarge, modifier = Modifier.weight(1f))
        FilledTonalIconButton(onClick = { onChange((value - step).coerceIn(range)) }, enabled = value > range.first, colors = IconButtonDefaults.filledTonalIconButtonColors(containerColor = Spindle.colors.chip)) {
            Icon(Icons.Filled.Remove, "Less")
        }
        FilledTonalIconButton(onClick = { onChange((value + step).coerceIn(range)) }, enabled = value < range.last, colors = IconButtonDefaults.filledTonalIconButtonColors(containerColor = Spindle.colors.chip)) {
            Icon(Icons.Filled.Add, "More")
        }
    }
}

/** Your name, as teammates see it, and the time zone days are counted in. */
@Composable
private fun ProfilePage(me: MeResponse, choosingZone: Boolean, go: (SettingsPage) -> Unit, onBack: () -> Unit) {
    val app = LocalApp.current
    var fullName by rememberSaveableString(me.fullName)
    var zone by rememberSaveableString(me.timeZone)
    var saving by remember { mutableStateOf(false) }
    var error by remember { mutableStateOf<String?>(null) }
    if (choosingZone) {
        TimeZonePage(zone, { zone = it; go(SettingsPage.Profile) }) { go(SettingsPage.Profile) }
        return
    }
    val device = ZoneId.systemDefault().id
    SubPage("Profile", onBack, actions = {
        BarAction(if (saving) "Saving…" else "Save", {
            if (fullName.isBlank()) { error = "Enter your name."; return@BarAction }
            saving = true
            app.launch {
                runCatching { app.repository.updateProfile(fullName.trim(), zone) }
                    .onSuccess { app.notices.show("Profile saved"); onBack() }.onFailure { error = it.message }
                saving = false
            }
        }, enabled = !saving && (fullName != me.fullName || zone != me.timeZone), prominent = true)
    }) {
        LazyColumn(contentPadding = PaddingValues(top = 8.dp, bottom = 32.dp)) {
            item {
                Card {
                    ValueRow("Email", me.email)
                    RowDivider()
                    Row(Modifier.padding(horizontal = 16.dp), verticalAlignment = Alignment.CenterVertically) {
                        Text("Name", style = MaterialTheme.typography.bodyLarge, modifier = Modifier.width(110.dp))
                        PlainField(fullName, { fullName = it }, "Full name", Modifier.weight(1f), KeyboardOptions(capitalization = KeyboardCapitalization.Words))
                    }
                }
            }
            item { SectionFooter("Your name is how teammates see you.") }
            item { Spacer(Modifier.height(20.dp)) }
            item {
                Card {
                    ValueRow("Time zone", zone.replace('_', ' '), onClick = { go(SettingsPage.TimeZone) })
                    if (zone != device) { RowDivider(); ActionRow("Use this phone's time zone (${device.replace('_', ' ')})", { zone = device }) }
                }
            }
            item { SectionFooter(error ?: "Days and times are counted in your time zone, on every device.", color = if (error != null) MaterialTheme.colorScheme.error else MaterialTheme.colorScheme.onSurfaceVariant) }
        }
    }
}

@Composable
private fun rememberSaveableString(initial: String): MutableState<String> = androidx.compose.runtime.saveable.rememberSaveable { mutableStateOf(initial) }

/** Every time zone, searchable by place. */
@Composable
private fun TimeZonePage(selection: String, onPick: (String) -> Unit, onBack: () -> Unit) {
    var search by remember { mutableStateOf("") }
    val zones = remember { ZoneId.getAvailableZoneIds().filter { it.contains('/') && !it.startsWith("Etc/") && !it.startsWith("SystemV/") }.sorted() }
    val term = search.trim()
    val shown = zones.filter { term.isEmpty() || it.replace('_', ' ').contains(term, true) }
    SubPage("Time Zone", onBack) {
        SearchField(search, { search = it }, "Search, e.g. Zagreb")
        LazyColumn(contentPadding = PaddingValues(top = 8.dp, bottom = 32.dp)) {
            cardItems(shown, key = { it }) { index, zone ->
                CardRow(index, shown.size, onClick = { onPick(zone) }) {
                    Row(Modifier.fillMaxWidth().heightIn(min = 48.dp).padding(horizontal = 16.dp), verticalAlignment = Alignment.CenterVertically) {
                        Text(zone.replace('_', ' '), Modifier.weight(1f))
                        if (zone == selection) Icon(Icons.Filled.Check, "Chosen", tint = Spindle.colors.brandText)
                    }
                }
            }
        }
    }
}

/**
 * Where the organization's trial or plan stands, for its owners and admins on the hosted service. It only says so:
 * as on the iPhone, choosing or managing a plan is left to the web, without a link from here, since store apps may
 * not lead to buying outside the store.
 */
@Composable
private fun PlanPage(state: AppState, organization: OrganizationSummary, onBack: () -> Unit) {
    val app = LocalApp.current
    var subscription by remember { mutableStateOf<SubscriptionResponse?>(null) }
    var error by remember { mutableStateOf<String?>(null) }
    LaunchedEffect(organization.id) {
        runCatching { app.repository.subscription(organization.id) }
            .onSuccess { subscription = it; error = null }
            .onFailure { if (it is kotlinx.coroutines.CancellationException) throw it; error = it.message ?: "Couldn't load the plan." }
    }
    SubPage("Plan", onBack) {
        val loaded = subscription
        when {
            loaded != null -> {
                val plan = PlanDescription.of(loaded, organization.name, state.time())
                val color = when (plan.tone) {
                    PlanDescription.Tone.Good -> Spindle.colors.success
                    PlanDescription.Tone.Neutral -> MaterialTheme.colorScheme.onSurfaceVariant
                    PlanDescription.Tone.Bad -> MaterialTheme.colorScheme.error
                }
                LazyColumn(contentPadding = PaddingValues(top = 8.dp, bottom = 32.dp)) {
                    item {
                        Card {
                            Column(Modifier.padding(16.dp).semantics(mergeDescendants = true) {}, verticalArrangement = Arrangement.spacedBy(8.dp)) {
                                Text(plan.badge, style = MaterialTheme.typography.labelMedium.copy(fontWeight = FontWeight.SemiBold), color = color,
                                    modifier = Modifier.clip(RoundedCornerShape(50)).background(color.copy(alpha = 0.12f)).padding(horizontal = 8.dp, vertical = 3.dp))
                                Text(plan.title, style = MaterialTheme.typography.titleLarge.copy(fontWeight = FontWeight.SemiBold))
                                if (plan.text.isNotEmpty()) Text(plan.text, style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
                            }
                        }
                    }
                    item { Spacer(Modifier.height(20.dp)) }
                    item { Card { ValueRow("Seats", if (loaded.seats == 1) "1 person" else "${loaded.seats} people") } }
                    item { SectionFooter("A seat is a person in at least one of ${organization.name}'s workspaces.") }
                }
            }
            error != null -> SectionFooter(error!!, Modifier.padding(top = 16.dp), color = MaterialTheme.colorScheme.error)
            else -> Box(Modifier.fillMaxWidth().padding(top = 32.dp), contentAlignment = Alignment.Center) { CircularProgressIndicator(Modifier.size(24.dp), strokeWidth = 2.dp) }
        }
    }
}

/** A new password; changing it signs you out everywhere else. */
@Composable
private fun PasswordPage(onBack: () -> Unit) {
    val app = LocalApp.current
    var current by remember { mutableStateOf("") }
    var new by remember { mutableStateOf("") }
    var repeated by remember { mutableStateOf("") }
    var saving by remember { mutableStateOf(false) }
    var error by remember { mutableStateOf<String?>(null) }
    SubPage("Change Password", onBack, actions = {
        BarAction(if (saving) "Saving…" else "Change", {
            error = when {
                new.length < 8 -> "Use at least 8 characters."
                new != repeated -> "The new passwords don't match."
                else -> null
            }
            if (error != null) return@BarAction
            saving = true
            app.launch {
                runCatching { app.repository.changePassword(current, new) }
                    .onSuccess { app.notices.show("Password changed. Your other devices are signed out."); onBack() }
                    .onFailure { error = it.message }
                saving = false
            }
        }, enabled = !saving && current.isNotEmpty() && new.isNotEmpty(), prominent = true)
    }) {
        Column(Modifier.padding(top = 8.dp)) {
            Card { PlainField(current, { current = it }, "Current password", Modifier.padding(horizontal = 16.dp), KeyboardOptions(keyboardType = KeyboardType.Password), password = true) }
            Spacer(Modifier.height(20.dp))
            Card {
                PlainField(new, { new = it }, "New password", Modifier.padding(horizontal = 16.dp), KeyboardOptions(keyboardType = KeyboardType.Password), password = true)
                RowDivider()
                PlainField(repeated, { repeated = it }, "Repeat the new password", Modifier.padding(horizontal = 16.dp), KeyboardOptions(keyboardType = KeyboardType.Password), password = true)
            }
            SectionFooter(error ?: "At least 8 characters; a few words make a strong one. Changing it signs you out on every other device.",
                color = if (error != null) MaterialTheme.colorScheme.error else MaterialTheme.colorScheme.onSurfaceVariant)
        }
    }
}

/** The devices and browsers signed in to the account; any but this phone can be signed out. */
@Composable
private fun SessionsPage(onBack: () -> Unit) {
    val app = LocalApp.current
    val scope = rememberCoroutineScope()
    var sessions by remember { mutableStateOf<List<SessionResponse>>(emptyList()) }
    var error by remember { mutableStateOf<String?>(null) }
    var revoking by remember { mutableStateOf<SessionResponse?>(null) }
    var confirmingOthers by remember { mutableStateOf(false) }
    suspend fun load() {
        runCatching { app.repository.sessions() }
            .onSuccess { list -> sessions = list.sortedWith(compareBy<SessionResponse> { !it.isCurrent }.thenByDescending { it.lastActiveAt }); error = null }
            .onFailure { error = it.message }
    }
    LaunchedEffect(Unit) { load() }
    SubPage("Signed In", onBack) {
        LazyColumn(contentPadding = PaddingValues(top = 8.dp, bottom = 32.dp)) {
            cardItems(sessions, key = { it.id }) { index, session ->
                CardRow(index, sessions.size, onClick = if (session.isCurrent) null else ({ revoking = session })) {
                    Column(Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 12.dp), verticalArrangement = Arrangement.spacedBy(2.dp)) {
                        Text(session.deviceName ?: "Unknown device")
                        Text(if (session.isCurrent) "This phone" else "Last active ${relative(session.lastActiveAt)}", style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
                    }
                }
            }
            item { SectionFooter(error ?: "Tap a device you no longer use or don't recognize to sign it out.", color = if (error != null) MaterialTheme.colorScheme.error else MaterialTheme.colorScheme.onSurfaceVariant) }
            if (sessions.size > 1) {
                item { Spacer(Modifier.height(20.dp)) }
                item { Card { ActionRow("Sign Out Everywhere Else", { confirmingOthers = true }, destructive = true) } }
            }
        }
    }
    revoking?.let { session ->
        ConfirmDialog("Sign out ${session.deviceName ?: "this device"}?", "It has to sign in again to use Spindle.", "Sign Out",
            onConfirm = { scope.launch { runCatching { app.repository.revokeSession(session.id) }.onFailure { error = it.message }; load() } }, onDismiss = { revoking = null })
    }
    if (confirmingOthers) ConfirmDialog("Sign out everywhere else?", "Every other device and browser has to sign in again.", "Sign Out",
        onConfirm = { scope.launch { runCatching { app.repository.revokeOtherSessions() }.onSuccess { app.notices.show("Signed out everywhere else") }.onFailure { error = it.message }; load() } },
        onDismiss = { confirmingOthers = false })
}

/** "5 minutes ago", "yesterday", "3 days ago". */
private fun relative(value: String): String {
    val then = runCatching { Instant.parse(value) }.getOrNull() ?: return value
    val minutes = Duration.between(then, Instant.now()).toMinutes()
    return when {
        minutes < 1 -> "just now"
        minutes < 60 -> "$minutes minute${if (minutes == 1L) "" else "s"} ago"
        minutes < 24 * 60 -> "${minutes / 60} hour${if (minutes / 60 == 1L) "" else "s"} ago"
        minutes < 48 * 60 -> "yesterday"
        else -> "${minutes / (24 * 60)} days ago"
    }
}

/**
 * Deletes the account, after the password. An account without one is confirmed by a sign-in on this phone in the last
 * few minutes instead; the server says so when it has been longer.
 */
@Composable
private fun DeleteAccountPage(passwordless: Boolean, onBack: () -> Unit) {
    val app = LocalApp.current
    var password by remember { mutableStateOf("") }
    var error by remember { mutableStateOf<String?>(null) }
    var deleting by remember { mutableStateOf(false) }
    SubPage("Delete Account", onBack) {
        Column(Modifier.padding(top = 8.dp)) {
            Card {
                Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
                    Text("Workspaces only you use are deleted with everything in them. In workspaces you share, your time stays for the team's reports, without your name.")
                    Text("This cannot be undone. Export your data first if you want to keep it.", fontWeight = FontWeight.Medium)
                }
            }
            if (!passwordless) {
                Spacer(Modifier.height(20.dp))
                Card { PlainField(password, { password = it }, "Your password", Modifier.padding(horizontal = 16.dp), KeyboardOptions(keyboardType = KeyboardType.Password), password = true) }
            }
            error?.let { SectionFooter(it, color = MaterialTheme.colorScheme.error) }
            Spacer(Modifier.height(20.dp))
            Card {
                ActionRow(if (deleting) "Deleting…" else "Delete My Account", {
                    deleting = true
                    app.launch { runCatching { app.repository.deleteAccount(password.takeUnless { passwordless }) }.onFailure { error = it.message }; deleting = false }
                }, destructive = true, enabled = (passwordless || password.isNotEmpty()) && !deleting)
            }
        }
    }
}

