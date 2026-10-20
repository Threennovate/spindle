package com.spindletrack.android.ui

import android.content.ActivityNotFoundException
import android.net.Uri
import androidx.browser.customtabs.CustomTabsIntent
import androidx.compose.animation.animateContentSize
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.text.BasicTextField
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalFocusManager
import androidx.compose.ui.platform.LocalUriHandler
import androidx.compose.ui.res.vectorResource
import androidx.compose.ui.text.LinkAnnotation
import androidx.compose.ui.text.SpanStyle
import androidx.compose.ui.text.TextLinkStyles
import androidx.compose.ui.text.buildAnnotatedString
import androidx.compose.ui.text.withLink
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.input.KeyboardCapitalization
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.text.input.VisualTransformation
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.LifecycleResumeEffect
import com.spindletrack.android.ExternalSignInReturn
import com.spindletrack.android.R
import com.spindletrack.android.data.*
import com.spindletrack.android.ui.theme.Spindle
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.launch

private enum class AuthMode { SignIn, Register, ForgotPassword, ResetSent, Server }

private const val PasswordMinLength = 8

private fun emailProblem(email: String): String? = when {
    email.isEmpty() -> "Enter your email address."
    !Regex("[^\\s@]+@[^\\s@]+\\.[^\\s@]+").matches(email) -> "Enter a valid email address."
    else -> null
}

/** Signing in, creating an account and asking for a new password, as on the iPhone. */
@Composable
fun AuthScreen(repository: SpindleRepository, state: AppState) {
    val options = LocalLaunchOptions.current
    var mode by remember {
        mutableStateOf(when (options.auth) { "register" -> AuthMode.Register; "forgotPassword" -> AuthMode.ForgotPassword; else -> AuthMode.SignIn })
    }
    // Shared by every mode, so switching between them keeps what was typed.
    var email by remember { mutableStateOf(if (state.sessionExpired) state.me?.email.orEmpty() else "") }
    var sentTo by remember { mutableStateOf("") }
    LaunchedEffect(Unit) { if (state.serverInfo == null) repository.loadServerInfo() }

    Column(
        Modifier.fillMaxSize().verticalScroll(rememberScrollState()).systemBarsPadding().imePadding().padding(horizontal = 24.dp).padding(bottom = 32.dp).animateContentSize(),
        verticalArrangement = Arrangement.spacedBy(28.dp),
    ) {
        Box(Modifier.fillMaxWidth().padding(top = 36.dp), contentAlignment = Alignment.Center) { Logo(height = 32.dp) }
        val (title, description) = when (mode) {
            AuthMode.SignIn -> "Sign in" to (if (state.sessionExpired) "Your session ended. Sign in again to send the changes saved on this phone." else "Welcome back. Sign in to continue tracking your time.")
            AuthMode.Register -> "Create your account" to "Start tracking your time in less than a minute."
            AuthMode.ForgotPassword -> "Forgot your password?" to "Enter the email you sign in with. We'll send you a link to choose a new password."
            AuthMode.ResetSent -> "Check your inbox" to "If an account uses $sentTo, we sent it a link to choose a new password."
            AuthMode.Server -> "Your own server" to "Use a Spindle server you run yourself. You sign in to each server separately."
        }
        Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
            Text(title, style = MaterialTheme.typography.headlineLarge)
            Text(description, style = MaterialTheme.typography.bodyLarge, color = MaterialTheme.colorScheme.onSurfaceVariant)
        }
        if (mode == AuthMode.SignIn || mode == AuthMode.Register) ExternalSignIn(repository, state)
        when (mode) {
            AuthMode.SignIn -> SignInForm(repository, state, email, { email = it }, forgot = { mode = AuthMode.ForgotPassword })
            AuthMode.Register -> RegisterForm(repository, state, email) { email = it }
            AuthMode.ForgotPassword -> ForgotForm(repository, email, { email = it }) { sentTo = it; mode = AuthMode.ResetSent }
            AuthMode.ResetSent -> ProminentButton("Back to Sign In", { mode = AuthMode.SignIn })
            AuthMode.Server -> ServerForm(repository) { mode = AuthMode.SignIn }
        }
        Column(Modifier.fillMaxWidth(), horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.spacedBy(4.dp)) {
            when (mode) {
                AuthMode.SignIn -> if (state.serverInfo?.registrationOpen != false && !state.sessionExpired) Row(verticalAlignment = Alignment.CenterVertically) {
                    Text("New to Spindle? ", color = MaterialTheme.colorScheme.onSurfaceVariant)
                    BrandLink("Create an account", { mode = AuthMode.Register })
                }
                AuthMode.Register -> Row(verticalAlignment = Alignment.CenterVertically) {
                    Text("Already have an account? ", color = MaterialTheme.colorScheme.onSurfaceVariant)
                    BrandLink("Sign in", { mode = AuthMode.SignIn })
                }
                AuthMode.ForgotPassword, AuthMode.Server -> BrandLink("Back to sign in", { mode = AuthMode.SignIn })
                AuthMode.ResetSent -> {}
            }
            if (mode == AuthMode.SignIn && !state.sessionExpired) {
                Spacer(Modifier.height(20.dp))
                Text(
                    repository.serverUrl.removePrefix("https://").removePrefix("http://"),
                    style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
                BrandLink("Change server", { mode = AuthMode.Server }, style = MaterialTheme.typography.bodyMedium)
            }
        }
    }
}

/** A plain text field for a LabeledField's card, with a placeholder. */
@Composable
fun PlainField(
    value: String, onChange: (String) -> Unit, placeholder: String, modifier: Modifier = Modifier,
    keyboard: KeyboardOptions = KeyboardOptions.Default, onSubmit: (() -> Unit)? = null,
    password: Boolean = false, enabled: Boolean = true, singleLine: Boolean = true, end: Boolean = false,
    /** What the field is called for TalkBack, when a label above it says more than the placeholder. */
    label: String = placeholder,
) {
    BasicTextField(
        value, onChange, modifier.fillMaxWidth().padding(vertical = 14.dp).semantics { contentDescription = label }, enabled = enabled, singleLine = singleLine,
        textStyle = MaterialTheme.typography.bodyLarge.copy(color = MaterialTheme.colorScheme.onSurface, textAlign = if (end) TextAlign.End else TextAlign.Start),
        cursorBrush = SolidColor(Spindle.colors.brandText),
        keyboardOptions = keyboard, keyboardActions = KeyboardActions(onAny = { onSubmit?.invoke() }),
        visualTransformation = if (password) PasswordVisualTransformation() else VisualTransformation.None,
        decorationBox = { inner ->
            Box(contentAlignment = if (end) Alignment.CenterEnd else Alignment.CenterStart) {
                if (value.isEmpty()) Text(placeholder, style = MaterialTheme.typography.bodyLarge, color = MaterialTheme.colorScheme.onSurfaceVariant.copy(alpha = 0.7f), textAlign = if (end) TextAlign.End else TextAlign.Start, modifier = Modifier.fillMaxWidth())
                inner()
            }
        },
    )
}

/**
 * Continue with Google or Apple, above the email form when the server offers them, for signing in and creating an
 * account alike. The provider's page opens in a browser tab over the app; AuthCallbackActivity brings the answer back.
 */
@Composable
private fun ExternalSignIn(repository: SpindleRepository, state: AppState) {
    val context = LocalContext.current
    val returned by ExternalSignInReturn.pending
    // The provider whose page is open. Saved, because Android may recreate the app while the browser is in front.
    var leaving by rememberSaveable { mutableStateOf<String?>(null) }
    var exchanging by remember { mutableStateOf(false) }
    var error by remember { mutableStateOf<String?>(null) }
    LaunchedEffect(returned) {
        val address = returned ?: return@LaunchedEffect
        // Only an answer to a sign-in started here counts.
        if (leaving != null) {
            when (val callback = ExternalCallback.parse(address)) {
                is ExternalCallback.Code -> {
                    exchanging = true; error = null
                    runCatching { repository.finishExternalSignIn(callback.code) }.onFailure {
                        if (it is CancellationException) throw it
                        error = it.message ?: "Couldn't sign in."
                    }
                    repository.clearError()
                    exchanging = false
                }
                is ExternalCallback.Failed -> { repository.forgetExternalSignIn(); error = "Couldn't sign in. Please try again." }
                ExternalCallback.Cancelled, null -> repository.forgetExternalSignIn()
            }
            leaving = null
        }
        // Last: the answer is this effect's key, so clearing it earlier would cancel the exchange halfway, after the
        // verifier was already used up.
        ExternalSignInReturn.pending.value = null
    }
    // Back without an answer: the person closed the browser tab.
    LifecycleResumeEffect(Unit) {
        if (leaving != null && ExternalSignInReturn.pending.value == null && !exchanging) { leaving = null; repository.forgetExternalSignIn() }
        onPauseOrDispose {}
    }
    val providers = state.serverInfo?.signInProviders.orEmpty()
    if (providers.isEmpty()) return

    fun start(provider: String) {
        error = null
        leaving = provider
        try {
            CustomTabsIntent.Builder().setShowTitle(true).build().launchUrl(context, Uri.parse(repository.startExternalSignIn(provider)))
        } catch (e: ActivityNotFoundException) {
            repository.forgetExternalSignIn(); leaving = null
            error = "There is no browser on this phone to sign in with."
        }
    }
    Column(verticalArrangement = Arrangement.spacedBy(12.dp)) {
        error?.let { FormBanner(it) }
        providers.forEach { provider ->
            val mark = when (provider.name) {
                "google" -> ImageVector.vectorResource(R.drawable.ic_google)
                "apple" -> ImageVector.vectorResource(R.drawable.ic_apple)
                else -> null
            }
            SecondaryButton(
                when { leaving != provider.name -> "Continue with ${provider.displayName}"; exchanging -> "Signing in…"; else -> "Opening ${provider.displayName}…" },
                { start(provider.name) }, enabled = leaving == null, icon = mark, tintIcon = provider.name != "google",
            )
        }
        // Continuing can make an account, so it is agreeing to the terms, as on the web.
        state.serverInfo?.termsUrl?.let { terms ->
            val link = TextLinkStyles(SpanStyle(color = Spindle.colors.brandText))
            Text(
                buildAnnotatedString {
                    append("By continuing, you agree to the ")
                    withLink(LinkAnnotation.Url(terms, link)) { append("terms of service") }
                    state.serverInfo?.privacyUrl?.let { privacy ->
                        append(" and ")
                        withLink(LinkAnnotation.Url(privacy, link)) { append("privacy policy") }
                    }
                    append(".")
                },
                style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant,
                textAlign = TextAlign.Center, modifier = Modifier.fillMaxWidth(),
            )
        }
        Row(Modifier.padding(top = 8.dp), verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(12.dp)) {
            HorizontalDivider(Modifier.weight(1f), color = Spindle.colors.hairline)
            Text("or with email", style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
            HorizontalDivider(Modifier.weight(1f), color = Spindle.colors.hairline)
        }
    }
}

private val EmailKeyboard =KeyboardOptions(keyboardType = KeyboardType.Email, capitalization = KeyboardCapitalization.None, autoCorrectEnabled = false, imeAction = ImeAction.Next)

@Composable
private fun SignInForm(repository: SpindleRepository, state: AppState, email: String, setEmail: (String) -> Unit, forgot: () -> Unit) {
    val scope = rememberCoroutineScope()
    val focus = LocalFocusManager.current
    val passwordFocus = remember { FocusRequester() }
    var password by remember { mutableStateOf("") }
    var errors by remember { mutableStateOf(mapOf<String, String>()) }
    var formError by remember { mutableStateOf<String?>(null) }
    var pending by remember { mutableStateOf(false) }
    fun submit() {
        if (pending) return
        val trimmed = email.trim()
        errors = listOfNotNull(emailProblem(trimmed)?.let { "email" to it }, if (password.isEmpty()) "password" to "Enter your password." else null).toMap()
        if (errors.isNotEmpty()) return
        focus.clearFocus()
        pending = true; formError = null
        scope.launch {
            runCatching { repository.signIn(trimmed, password) }.onFailure { formError = it.message ?: "Couldn't sign in." }
            repository.clearError()
            pending = false
        }
    }
    Column(verticalArrangement = Arrangement.spacedBy(18.dp)) {
        formError?.let { FormBanner(it) }
        LabeledField("Email", errors["email"]) {
            PlainField(email, setEmail, "you@example.com", keyboard = EmailKeyboard, onSubmit = { passwordFocus.requestFocus() }, enabled = !state.sessionExpired, label = "Email")
        }
        LabeledField("Password", errors["password"]) {
            PlainField(password, { password = it }, "Password", Modifier.focusRequester(passwordFocus), KeyboardOptions(keyboardType = KeyboardType.Password, imeAction = ImeAction.Go), ::submit, password = true)
        }
        Box(Modifier.fillMaxWidth(), contentAlignment = Alignment.CenterEnd) {
            BrandLink("Forgot your password?", forgot, style = MaterialTheme.typography.bodyMedium)
        }
        ProminentButton(if (pending) "Signing in…" else "Sign in", ::submit, busy = pending)
    }
}

@Composable
private fun RegisterForm(repository: SpindleRepository, state: AppState, email: String, setEmail: (String) -> Unit) {
    val scope = rememberCoroutineScope()
    val focus = LocalFocusManager.current
    val uri = LocalUriHandler.current
    var name by remember { mutableStateOf("") }
    var password by remember { mutableStateOf("") }
    var accepted by remember { mutableStateOf(false) }
    var errors by remember { mutableStateOf(mapOf<String, String>()) }
    var formError by remember { mutableStateOf<String?>(null) }
    var pending by remember { mutableStateOf(false) }
    val terms = state.serverInfo?.termsUrl
    fun submit() {
        val trimmed = email.trim()
        errors = listOfNotNull(
            if (name.isBlank()) "fullName" to "Enter your name." else null,
            emailProblem(trimmed)?.let { "email" to it },
            if (password.length < PasswordMinLength) "password" to "Use at least $PasswordMinLength characters." else null,
            if (terms != null && !accepted) "acceptTerms" to "Agree to the terms of service and privacy policy to create an account." else null,
        ).toMap()
        if (errors.isNotEmpty()) return
        focus.clearFocus()
        pending = true; formError = null
        scope.launch {
            runCatching { repository.register(name.trim(), trimmed, password, acceptTerms = accepted) }.onFailure { formError = it.message ?: "Couldn't create the account." }
            repository.clearError()
            pending = false
        }
    }
    Column(verticalArrangement = Arrangement.spacedBy(18.dp)) {
        formError?.let { FormBanner(it) }
        LabeledField("Full name", errors["fullName"]) {
            PlainField(name, { name = it }, "Your name", keyboard = KeyboardOptions(capitalization = KeyboardCapitalization.Words, imeAction = ImeAction.Next))
        }
        LabeledField("Email", errors["email"]) { PlainField(email, setEmail, "you@example.com", keyboard = EmailKeyboard, label = "Email") }
        LabeledField("Password", errors["password"], hint = "At least $PasswordMinLength characters.") {
            PlainField(password, { password = it }, "Password", keyboard = KeyboardOptions(keyboardType = KeyboardType.Password, imeAction = ImeAction.Go), onSubmit = ::submit, password = true)
        }
        if (terms != null) Column {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Checkbox(accepted, { accepted = it }, colors = CheckboxDefaults.colors(checkedColor = MaterialTheme.colorScheme.primary))
                Column {
                    Text("I agree to the terms of service and the privacy policy.", style = MaterialTheme.typography.bodyMedium)
                    Row {
                        BrandLink("Terms of service", { uri.openUri(terms) }, style = MaterialTheme.typography.bodyMedium)
                        state.serverInfo?.privacyUrl?.let { privacy ->
                            Text("  ·  ", color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.align(Alignment.CenterVertically))
                            BrandLink("Privacy policy", { uri.openUri(privacy) }, style = MaterialTheme.typography.bodyMedium)
                        }
                    }
                }
            }
            errors["acceptTerms"]?.let { Text(it, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.error) }
        }
        ProminentButton(if (pending) "Creating account…" else "Create account", ::submit, busy = pending)
    }
}

@Composable
private fun ForgotForm(repository: SpindleRepository, email: String, setEmail: (String) -> Unit, sent: (String) -> Unit) {
    val scope = rememberCoroutineScope()
    var error by remember { mutableStateOf<String?>(null) }
    var formError by remember { mutableStateOf<String?>(null) }
    var pending by remember { mutableStateOf(false) }
    fun submit() {
        val trimmed = email.trim()
        error = emailProblem(trimmed)
        if (error != null) return
        pending = true; formError = null
        scope.launch {
            runCatching { repository.forgotPassword(trimmed) }.onSuccess { sent(trimmed) }.onFailure { formError = it.message }
            pending = false
        }
    }
    Column(verticalArrangement = Arrangement.spacedBy(18.dp)) {
        formError?.let { FormBanner(it) }
        LabeledField("Email", error) {
            PlainField(email, setEmail, "you@example.com", keyboard = EmailKeyboard.copy(imeAction = ImeAction.Send), onSubmit = ::submit, label = "Email")
        }
        ProminentButton(if (pending) "Sending…" else "Send reset link", ::submit, busy = pending)
    }
}

/** Chooses the server: Spindle Cloud, or one the person runs. */
@Composable
private fun ServerForm(repository: SpindleRepository, done: () -> Unit) {
    val scope = rememberCoroutineScope()
    var address by remember { mutableStateOf(repository.serverUrl) }
    var error by remember { mutableStateOf<String?>(null) }
    var pending by remember { mutableStateOf(false) }
    fun use(url: String) {
        var text = url.trim()
        if (!text.contains("://")) text = "https://$text"
        pending = true; error = null
        scope.launch {
            runCatching { repository.configureServer(text); repository.serverInfo() }
                .onSuccess { done() }.onFailure { error = it.message ?: "Couldn't reach that server." }
            pending = false
        }
    }
    Column(verticalArrangement = Arrangement.spacedBy(18.dp)) {
        LabeledField("Server address", error, hint = "For example spindle.example.com. Only a server on your own network can be plain http.") {
            PlainField(address, { address = it }, "spindle.example.com", label = "Server address", keyboard = KeyboardOptions(keyboardType = KeyboardType.Uri, autoCorrectEnabled = false, imeAction = ImeAction.Go), onSubmit = { use(address) })
        }
        ProminentButton(if (pending) "Checking…" else "Use this server", { use(address) }, busy = pending)
        if (repository.serverUrl != com.spindletrack.android.BuildConfig.SERVER_URL) {
            SecondaryButton("Use Spindle Cloud", { use(com.spindletrack.android.BuildConfig.SERVER_URL) })
        }
    }
}

