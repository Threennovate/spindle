package com.spindletrack.android.data

import java.net.URI
import java.net.URLDecoder
import java.net.URLEncoder
import java.security.MessageDigest
import java.security.SecureRandom
import java.util.Base64

// Continue with Google or Apple: the browser signs in on the server, which sends it back to this app with a one-time
// code. Only the app that started can trade it for a session, because only it knows the PKCE verifier.

/** Where the browser comes back to. The server only accepts this address (or a loopback one) from an app. */
const val ExternalRedirectUri = "spindle://auth/callback"

/** PKCE (RFC 7636): a secret the app keeps, and its hash, which the server gets when the sign-in starts. */
object Pkce {
    private val base64 = Base64.getUrlEncoder().withoutPadding()

    /** 32 random bytes, base64url without padding: 43 characters. */
    fun verifier(random: SecureRandom = SecureRandom()): String = base64.encodeToString(ByteArray(32).also(random::nextBytes))

    /** The S256 challenge: base64url of the SHA-256 of the verifier's ASCII, without padding. */
    fun challenge(verifier: String): String = base64.encodeToString(MessageDigest.getInstance("SHA-256").digest(verifier.toByteArray(Charsets.US_ASCII)))
}

/**
 * The page that starts signing in with [provider]. The terms are shown next to the buttons, so continuing agrees to
 * them; they only count when a new account is made. The time zone is a new account's.
 */
fun externalSignInUrl(server: String, provider: String, codeChallenge: String, timeZone: String, invitation: String? = null): String {
    fun encode(value: String) = URLEncoder.encode(value, "UTF-8").replace("+", "%20")
    val query = listOfNotNull(
        "redirectUri" to ExternalRedirectUri, "codeChallenge" to codeChallenge, "timeZone" to timeZone, "acceptTerms" to "true",
        invitation?.let { "invitation" to it },
    ).joinToString("&") { (name, value) -> "$name=${encode(value)}" }
    return "${server.trimEnd('/')}/api/auth/external/${encode(provider)}/start?$query"
}

/** What the browser came back with. */
sealed interface ExternalCallback {
    data class Code(val code: String) : ExternalCallback
    /** The person turned back at Google or Apple; nothing to say. */
    data object Cancelled : ExternalCallback
    data class Failed(val error: String) : ExternalCallback

    companion object {
        /** The callback in [url], or null when it isn't `spindle://auth/callback`. */
        fun parse(url: String): ExternalCallback? {
            val uri = runCatching { URI(url) }.getOrNull() ?: return null
            if (!uri.scheme.equals("spindle", ignoreCase = true) || !uri.host.equals("auth", ignoreCase = true) || uri.path != "/callback") return null
            val query = uri.rawQuery.orEmpty().split('&').filter { it.isNotEmpty() }.associate { pair ->
                val (name, value) = pair.split('=', limit = 2).let { it[0] to it.getOrElse(1) { "" } }
                URLDecoder.decode(name, "UTF-8") to URLDecoder.decode(value, "UTF-8")
            }
            query["code"]?.takeIf { it.isNotEmpty() }?.let { return Code(it) }
            return when (val error = query["error"]) {
                "cancelled" -> Cancelled
                null, "" -> Failed("unknown")
                else -> Failed(error)
            }
        }
    }
}
