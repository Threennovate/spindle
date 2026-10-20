package com.spindletrack.android.ui

import androidx.compose.foundation.layout.*
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.BarChart
import androidx.compose.material.icons.filled.CalendarMonth
import androidx.compose.material.icons.filled.Folder
import androidx.compose.material.icons.filled.PersonOff
import androidx.compose.material.icons.filled.Settings
import androidx.compose.material.icons.filled.Timer
import androidx.compose.material.icons.outlined.BarChart
import androidx.compose.material.icons.outlined.CalendarMonth
import androidx.compose.material.icons.outlined.Folder
import androidx.compose.material.icons.outlined.Settings
import androidx.compose.material.icons.outlined.Timer
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.spindletrack.android.data.*
import com.spindletrack.android.ui.theme.Spindle
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.launch
import java.time.Instant

const val ShortcutStart = "com.spindletrack.android.START_TIMER"
const val ShortcutStop = "com.spindletrack.android.STOP_TIMER"

/** The tabs of the signed-in app. */
enum class MainTab(val title: String, val icon: ImageVector, val selectedIcon: ImageVector) {
    Timer("Timer", Icons.Outlined.Timer, Icons.Filled.Timer),
    Calendar("Calendar", Icons.Outlined.CalendarMonth, Icons.Filled.CalendarMonth),
    Reports("Reports", Icons.Outlined.BarChart, Icons.Filled.BarChart),
    Projects("Projects", Icons.Outlined.Folder, Icons.Filled.Folder),
    Settings("Settings", Icons.Outlined.Settings, Icons.Filled.Settings),
}

/**
 * How a debug run starts, from the intent's extras, so a script can take screenshots without anyone tapping, as the
 * iPhone app's `SPINDLE_AUTOMATION_*` variables do: `email`, `password` and `server` sign in, `tab` opens a tab, `edit`
 * (`first`, `running`, `new`) opens the editor, `picker` (`project`, `tags`), `report` (`summary`, `detailed`,
 * `weekly`), `filters`, `manage` (`projects`, `clients`, `tags`), `project` (a name, or `new`), `auth` (`signIn`,
 * `register`, `forgotPassword`) and `description` (typed into the timer).
 */
data class LaunchOptions(
    val email: String? = null, val password: String? = null, val server: String? = null,
    val tab: MainTab? = null, val auth: String? = null, val description: String? = null, val edit: String? = null, val picker: String? = null,
    val report: String? = null, val filters: Boolean = false, val manage: String? = null, val project: String? = null,
)

val LocalLaunchOptions = staticCompositionLocalOf { LaunchOptions() }

/** An entry open in the editor, or the time for a new one. */
data class EntryEditing(
    val entry: TimeEntryResponse? = null,
    val newRange: Pair<Instant, Instant>? = null,
    /** The timer couldn't stop without details the workspace requires; saving stops it. */
    val stopping: Boolean = false,
    val id: Long = System.nanoTime(),
)

/** What every screen can ask of the app: open an entry, start and stop the timer, show a notice. */
class AppController(val repository: SpindleRepository, val notices: Notices, private val scope: CoroutineScope) {
    var editing by mutableStateOf<EntryEditing?>(null)
    var confirmingSignOut by mutableStateOf(false)

    fun launch(block: suspend () -> Unit) { scope.launch { block() } }

    suspend fun start(draft: EntryDraft): Boolean = try {
        if (repository.state.value.readOnlyOrganization != null) { notices.show(ReadOnlyMessage, error = true); false }
        else { repository.startTimer(draft); true }
    } catch (e: CancellationException) {
        throw e
    } catch (e: Exception) {
        notices.show(e, "Couldn't start the timer."); false
    }

    /** Stops the running timer; when the workspace requires details it lacks, opens the entry to add them. */
    suspend fun stop(): Boolean {
        val running = repository.state.value.running ?: return false
        if (repository.state.value.missingDetails(running.draft().copy(stop = Instant.now().toString())).isNotEmpty()) {
            notices.show("Add the details this workspace requires to stop the timer.", error = true)
            editing = EntryEditing(running, stopping = true)
            return false
        }
        return try {
            repository.stopTimer(running); true
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            notices.show(e, "Couldn't stop the timer.")
            editing = EntryEditing(running, stopping = true)
            false
        }
    }

    suspend fun continueEntry(entry: TimeEntryResponse) {
        start(entry.draft().copy(start = Instant.now().toString(), stop = null))
    }

    suspend fun duplicate(entry: TimeEntryResponse) {
        try {
            repository.saveEntry(entry.draft())
            notices.show("Time entry duplicated")
        } catch (e: Exception) { notices.show(e, "Couldn't duplicate the time entry.") }
    }

    suspend fun delete(entry: TimeEntryResponse) {
        try {
            repository.deleteEntry(entry)
            notices.show("Time entry deleted")
        } catch (e: Exception) { notices.show(e, "Couldn't delete the time entry.") }
    }

    /** Changes the running entry, e.g. its project, saving it at once. */
    suspend fun updateRunning(change: (EntryDraft) -> EntryDraft) {
        val running = repository.state.value.running ?: return
        try {
            repository.saveEntry(change(running.draft()), running)
        } catch (e: Exception) { notices.show(e, "Couldn't save the running entry.") }
    }

    fun requestSignOut() {
        val state = repository.state.value
        if (state.pendingCount > 0 || state.failedChanges.isNotEmpty()) confirmingSignOut = true
        else launch { runCatching { repository.signOut() }.onFailure { notices.show(it, "Couldn't sign out.") } }
    }
}

val LocalApp = staticCompositionLocalOf<AppController> { error("No app") }

@Composable
fun SpindleApp(repository: SpindleRepository, options: LaunchOptions = LaunchOptions(), shortcut: String? = null, shortcutDone: () -> Unit = {}) {
    val state by repository.state.collectAsStateWithLifecycle()
    val scope = rememberCoroutineScope()
    val notices = remember { Notices() }
    val app = remember { AppController(repository, notices, scope) }
    var started by remember { mutableStateOf(false) }
    LaunchedEffect(repository) {
        runCatching {
            repository.initialize()
            if (options.email != null && options.password != null && repository.state.value.me == null) {
                options.server?.let { repository.configureServer(it) }
                repository.signIn(options.email, options.password)
            }
        }.onFailure { notices.show(it, "Couldn't sign in.") }
        started = true
    }

    // The launcher's shortcuts, as Siri's on the iPhone: start a timer, or stop the one running.
    LaunchedEffect(shortcut, started, state.workspaceId) {
        if (shortcut == null || !started || state.me == null || state.workspaceId == null) return@LaunchedEffect
        val action = shortcut
        shortcutDone()
        app.launch {
            when (action) {
                ShortcutStart -> if (repository.state.value.running == null && app.start(EntryDraft())) notices.show("Timer started")
                ShortcutStop -> if (repository.state.value.running == null) notices.show("No timer is running.") else if (app.stop()) notices.show("Timer stopped")
            }
        }
    }

    CompositionLocalProvider(LocalApp provides app, LocalNotices provides notices, LocalLaunchOptions provides options) {
        Surface(Modifier.fillMaxSize(), color = MaterialTheme.colorScheme.background) { Box(Modifier.fillMaxSize()) {
            when {
                !started && state.me == null -> LaunchView()
                state.me == null || state.sessionExpired -> AuthScreen(repository, state)
                state.workspaceId == null -> NoWorkspaceView()
                else -> MainTabs(state, options)
            }
            NoticeHost(notices, Modifier.align(Alignment.BottomCenter).navigationBarsPadding().padding(bottom = if (state.me != null) 80.dp else 0.dp))
        } }
        if (app.confirmingSignOut) {
            val waiting = state.pendingCount
            ConfirmDialog(
                "Sign out with changes still waiting?",
                if (waiting == 1) "1 change hasn't reached the server yet. If you sign out now, it is lost."
                else "$waiting changes haven't reached the server yet. If you sign out now, they are lost.",
                "Sign Out Anyway", onConfirm = { app.launch { repository.signOut(force = true) } }, onDismiss = { app.confirmingSignOut = false },
            )
        }
    }
}

/** While the app picks up the session from the last launch. */
@Composable
private fun LaunchView() {
    Column(Modifier.fillMaxSize(), verticalArrangement = Arrangement.Center, horizontalAlignment = Alignment.CenterHorizontally) {
        Logo(height = 34.dp)
        Spacer(Modifier.height(24.dp))
        CircularProgressIndicator(Modifier.size(24.dp), strokeWidth = 2.5.dp, color = MaterialTheme.colorScheme.onSurfaceVariant)
    }
}

/** The person was removed from their last workspace. */
@Composable
private fun NoWorkspaceView() {
    val app = LocalApp.current
    Box(Modifier.fillMaxSize().systemBarsPadding(), contentAlignment = Alignment.Center) {
        EmptyState(Icons.Filled.PersonOff, "You are not in a workspace", "You were removed from your last workspace. Ask an admin to invite you again.") {
            SecondaryButton("Sign Out", { app.requestSignOut() }, Modifier.widthIn(max = 240.dp))
        }
    }
}

@Composable
private fun MainTabs(state: AppState, options: LaunchOptions) {
    val app = LocalApp.current
    var tab by rememberSaveable { mutableStateOf(options.tab ?: MainTab.Timer) }
    Scaffold(
        containerColor = MaterialTheme.colorScheme.background,
        contentWindowInsets = WindowInsets(0),
        bottomBar = {
            NavigationBar(containerColor = MaterialTheme.colorScheme.surfaceContainer, tonalElevation = 0.dp) {
                MainTab.entries.forEach { item ->
                    NavigationBarItem(
                        selected = tab == item, onClick = { tab = item },
                        icon = { Icon(if (tab == item) item.selectedIcon else item.icon, null) },
                        label = { Text(item.title) },
                        colors = NavigationBarItemDefaults.colors(
                            selectedIconColor = Spindle.colors.brandText, selectedTextColor = Spindle.colors.brandText,
                            indicatorColor = Spindle.colors.chip,
                            unselectedIconColor = MaterialTheme.colorScheme.onSurfaceVariant, unselectedTextColor = MaterialTheme.colorScheme.onSurfaceVariant,
                        ),
                    )
                }
            }
        },
    ) { padding ->
        // Keyed by workspace, so switching starts every tab fresh instead of mixing two workspaces' data.
        key(state.workspaceId) {
            Box(Modifier.fillMaxSize().padding(padding).consumeWindowInsets(padding)) {
                when (tab) {
                    MainTab.Timer -> TimerTab(state)
                    MainTab.Calendar -> CalendarTab(state)
                    MainTab.Reports -> ReportsTab(state)
                    MainTab.Projects -> ManageTab(state)
                    MainTab.Settings -> SettingsTab(state)
                }
            }
        }
    }
    app.editing?.let { editing ->
        key(editing.id) { EntryEditor(state, editing, onDismiss = { app.editing = null }) }
    }
}

/** The account's time, for screens and the editor. */
@Composable
fun rememberTime(state: AppState): AccountTime = remember(state.me?.timeZone, state.me?.preferences) { state.time() }

