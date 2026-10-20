package com.spindletrack.android.ui

import androidx.activity.compose.BackHandler
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.slideInVertically
import androidx.compose.animation.slideOutVertically
import androidx.compose.foundation.ExperimentalFoundationApi
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.filled.CheckCircle
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.Warning
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.shadow
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.platform.LocalView
import androidx.compose.ui.window.Dialog
import androidx.compose.ui.window.DialogWindowProvider
import androidx.core.view.WindowCompat
import androidx.compose.ui.window.DialogProperties
import com.spindletrack.android.ui.theme.Spindle
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.delay

// The frame around screens: top bars, sheets, full-screen editors, and the short notices that say what happened.

// MARK: - Notices

/** A short message, e.g. that an entry was deleted or couldn't be saved. */
data class Notice(val text: String, val error: Boolean = false, val id: Long = System.nanoTime())

/** Shows one notice at a time, for a few seconds. */
class Notices {
    var current by mutableStateOf<Notice?>(null)
        private set

    fun show(text: String, error: Boolean = false) { current = Notice(text, error) }

    /** Shows what went wrong: the server's words, or `fallback` when it gave none. */
    fun show(error: Throwable, fallback: String) {
        if (error is CancellationException) return
        show(error.message?.takeIf { it.isNotBlank() } ?: fallback, error = true)
    }

    fun dismiss() { current = null }
}

val LocalNotices = staticCompositionLocalOf { Notices() }

@Composable
fun NoticeHost(notices: Notices, modifier: Modifier = Modifier) {
    val notice = notices.current
    LaunchedEffect(notice?.id) {
        if (notice != null) {
            delay(if (notice.error) 6000 else 3000)
            if (notices.current?.id == notice.id) notices.dismiss()
        }
    }
    var shown by remember { mutableStateOf(notice) }
    if (notice != null) shown = notice
    AnimatedVisibility(notice != null, modifier, enter = slideInVertically { it } + fadeIn(), exit = slideOutVertically { it } + fadeOut()) {
        shown?.let { item ->
            Row(
                Modifier.padding(horizontal = 16.dp, vertical = 8.dp).widthIn(max = 480.dp).fillMaxWidth()
                    .shadow(10.dp, RoundedCornerShape(14.dp))
                    .clip(RoundedCornerShape(14.dp)).background(MaterialTheme.colorScheme.surfaceContainerHigh)
                    .border(0.5.dp, Spindle.colors.hairline, RoundedCornerShape(14.dp))
                    .padding(start = 14.dp, end = 4.dp, top = 4.dp, bottom = 4.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Icon(
                    if (item.error) Icons.Filled.Warning else Icons.Filled.CheckCircle, null, Modifier.size(20.dp),
                    tint = if (item.error) MaterialTheme.colorScheme.error else Spindle.colors.brandText,
                )
                Spacer(Modifier.width(10.dp))
                Text(item.text, style = MaterialTheme.typography.bodyMedium, modifier = Modifier.weight(1f).padding(vertical = 8.dp))
                IconButton(onClick = { notices.dismiss() }) { Icon(Icons.Filled.Close, "Dismiss", Modifier.size(18.dp), tint = MaterialTheme.colorScheme.onSurfaceVariant) }
            }
        }
    }
}

// MARK: - Bars

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun barColors() = TopAppBarDefaults.topAppBarColors(
    containerColor = MaterialTheme.colorScheme.background, scrolledContainerColor = MaterialTheme.colorScheme.surfaceContainer,
    actionIconContentColor = Spindle.colors.brandText, navigationIconContentColor = Spindle.colors.brandText,
)

/** A screen's bar with its title in the middle and actions at both ends, as the iPhone's inline titles. */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun CenterBar(title: String, navigation: @Composable () -> Unit = {}, actions: @Composable RowScope.() -> Unit = {}, scroll: TopAppBarScrollBehavior? = null) {
    CenterAlignedTopAppBar(
        title = { Text(title, style = MaterialTheme.typography.titleMedium, maxLines = 1) },
        navigationIcon = navigation, actions = actions, colors = barColors(), scrollBehavior = scroll,
        windowInsets = WindowInsets.statusBars,
    )
}

/** A screen's bar with a large title that folds into the bar as the list scrolls, as the iPhone's large titles. */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun LargeBar(title: String, scroll: TopAppBarScrollBehavior, navigation: @Composable () -> Unit = {}, actions: @Composable RowScope.() -> Unit = {}) {
    LargeTopAppBar(
        title = { Text(title, maxLines = 1) }, navigationIcon = navigation, actions = actions, scrollBehavior = scroll,
        colors = TopAppBarDefaults.largeTopAppBarColors(
            containerColor = MaterialTheme.colorScheme.background, scrolledContainerColor = MaterialTheme.colorScheme.surfaceContainer,
            actionIconContentColor = Spindle.colors.brandText, navigationIconContentColor = Spindle.colors.brandText,
        ),
        windowInsets = WindowInsets.statusBars,
    )
}

@Composable
fun BackButton(onClick: () -> Unit) {
    IconButton(onClick = onClick) { Icon(Icons.AutoMirrored.Filled.ArrowBack, "Back") }
}

/** A text action in a bar, e.g. Save, in the brand colour and bold when it's the main one. */
@Composable
fun BarAction(text: String, onClick: () -> Unit, enabled: Boolean = true, prominent: Boolean = false) {
    TextButton(onClick = onClick, enabled = enabled, colors = ButtonDefaults.textButtonColors(contentColor = Spindle.colors.brandText)) {
        Text(text, style = MaterialTheme.typography.titleSmall.copy(fontWeight = if (prominent) FontWeight.SemiBold else FontWeight.Medium))
    }
}

// MARK: - Editors and sheets

/**
 * A form over the whole screen, as Android does full editors: close on the left, the title, and the main action on
 * the right. Its pages can open pickers of their own, which come back with the back gesture.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun FullScreenEditor(
    title: String,
    onDismiss: () -> Unit,
    confirm: String? = null,
    onConfirm: () -> Unit = {},
    confirmEnabled: Boolean = true,
    dismissLabel: String = "Close",
    /** Shows a back arrow instead of close, for a page within the editor. */
    back: Boolean = false,
    content: @Composable ColumnScope.() -> Unit,
) {
    Dialog(onDismissRequest = onDismiss, properties = DialogProperties(usePlatformDefaultWidth = false, decorFitsSystemWindows = false)) {
        // The dialog's own window draws behind the system bars, as the activity does, with icons that read on the page.
        val view = LocalView.current
        val dark = Spindle.colors.dark
        SideEffect {
            (view.parent as? DialogWindowProvider)?.window?.let { window ->
                window.setLayout(android.view.ViewGroup.LayoutParams.MATCH_PARENT, android.view.ViewGroup.LayoutParams.MATCH_PARENT)
                @Suppress("DEPRECATION") window.statusBarColor = android.graphics.Color.TRANSPARENT
                @Suppress("DEPRECATION") window.navigationBarColor = android.graphics.Color.TRANSPARENT
                WindowCompat.getInsetsController(window, view).apply { isAppearanceLightStatusBars = !dark; isAppearanceLightNavigationBars = !dark }
            }
        }
        Surface(Modifier.fillMaxSize(), color = MaterialTheme.colorScheme.background) {
            Column(Modifier.fillMaxSize().navigationBarsPadding().imePadding()) {
                TopAppBar(
                    title = { Text(title, style = MaterialTheme.typography.titleLarge, maxLines = 1) },
                    navigationIcon = { if (back) BackButton(onDismiss) else IconButton(onClick = onDismiss) { Icon(Icons.Filled.Close, dismissLabel) } },
                    actions = { if (confirm != null) BarAction(confirm, onConfirm, enabled = confirmEnabled, prominent = true) },
                    colors = barColors(), windowInsets = WindowInsets.statusBars,
                )
                content()
            }
        }
    }
}

/** A page inside an editor or a tab, with its own bar and a way back. */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun SubPage(title: String, onBack: () -> Unit, actions: @Composable RowScope.() -> Unit = {}, content: @Composable ColumnScope.() -> Unit) {
    BackHandler(onBack = onBack)
    Column(Modifier.fillMaxSize().background(MaterialTheme.colorScheme.background)) {
        TopAppBar(
            title = { Text(title, style = MaterialTheme.typography.titleLarge, maxLines = 1) },
            navigationIcon = { BackButton(onBack) }, actions = actions, colors = barColors(), windowInsets = WindowInsets.statusBars,
        )
        content()
    }
}

/** A sheet from the bottom for a quick choice, with a Done button. */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun PickerSheet(title: String, onDismiss: () -> Unit, actions: @Composable RowScope.() -> Unit = {}, skipPartial: Boolean = false, content: @Composable ColumnScope.() -> Unit) {
    val state = rememberModalBottomSheetState(skipPartiallyExpanded = skipPartial)
    ModalBottomSheet(onDismissRequest = onDismiss, sheetState = state, containerColor = MaterialTheme.colorScheme.background) {
        Row(Modifier.fillMaxWidth().padding(start = 24.dp, end = 12.dp, bottom = 4.dp), verticalAlignment = Alignment.CenterVertically) {
            Text(title, style = MaterialTheme.typography.titleLarge, modifier = Modifier.weight(1f))
            actions()
        }
        content()
    }
}

/** Long presses as well as taps, for rows with a menu. */
@OptIn(ExperimentalFoundationApi::class)
fun Modifier.combinedClickableCompat(onClick: () -> Unit, onLongClick: (() -> Unit)?): Modifier =
    combinedClickable(onClick = onClick, onLongClick = onLongClick)

/** A destructive question, e.g. before deleting, with the action in red. */
@Composable
fun ConfirmDialog(title: String, message: String, confirm: String, onConfirm: () -> Unit, onDismiss: () -> Unit, destructive: Boolean = true) {
    AlertDialog(
        onDismissRequest = onDismiss, title = { Text(title) }, text = { Text(message) },
        confirmButton = {
            TextButton(onClick = { onDismiss(); onConfirm() }) {
                Text(confirm, color = if (destructive) MaterialTheme.colorScheme.error else Spindle.colors.brandText, fontWeight = FontWeight.SemiBold)
            }
        },
        dismissButton = { TextButton(onClick = onDismiss) { Text("Cancel", color = Spindle.colors.brandText) } },
        containerColor = MaterialTheme.colorScheme.surfaceContainerHigh,
    )
}

/** Asks for a name, e.g. of a new tag. */
@Composable
fun NameDialog(title: String, label: String, initial: String = "", confirm: String, message: String? = null, onConfirm: (String) -> Unit, onDismiss: () -> Unit) {
    var name by remember { mutableStateOf(initial) }
    AlertDialog(
        onDismissRequest = onDismiss, title = { Text(title) },
        text = {
            Column(verticalArrangement = Arrangement.spacedBy(12.dp)) {
                if (message != null) Text(message, style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
                OutlinedTextField(name, { name = it }, label = { Text(label) }, singleLine = true, modifier = Modifier.fillMaxWidth())
            }
        },
        confirmButton = {
            TextButton(onClick = { onDismiss(); onConfirm(name.trim()) }, enabled = name.isNotBlank()) {
                Text(confirm, color = Spindle.colors.brandText, fontWeight = FontWeight.SemiBold)
            }
        },
        dismissButton = { TextButton(onClick = onDismiss) { Text("Cancel", color = Spindle.colors.brandText) } },
        containerColor = MaterialTheme.colorScheme.surfaceContainerHigh,
    )
}
