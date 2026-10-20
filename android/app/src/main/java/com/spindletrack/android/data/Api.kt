package com.spindletrack.android.data

import kotlinx.serialization.json.*
import java.net.URLEncoder

// Typed calls the screens make beside the repository's own, through its authenticated `request`.

private fun SpindleRepository.workspacePath(): String = "/api/workspaces/${state.value.workspaceId ?: error("Choose a workspace first.")}"

private inline fun <reified T> SpindleRepository.decode(element: JsonElement): T = json.decodeFromJsonElement(element)

/** Earlier entries whose description starts like `search`, to start again with their details. */
suspend fun SpindleRepository.suggestions(search: String, limit: Int = 8): List<TimeEntrySuggestion> {
    val query = URLEncoder.encode(search, "UTF-8")
    return decode(request("GET", "${workspacePath()}/time-entries/suggestions?search=$query&limit=$limit"))
}

suspend fun SpindleRepository.createProject(name: String, color: String): ProjectResponse {
    val project: ProjectResponse = decode(request("POST", "${workspacePath()}/projects", buildJsonObject {
        put("name", name); put("color", color); put("isPrivate", false); put("isBillable", false)
    }))
    refresh()
    return project
}

/** Creates a project or saves one; billing settings made on the web stay as they are. */
suspend fun SpindleRepository.saveProject(body: JsonObject, id: String?): ProjectResponse {
    val path = "${workspacePath()}/projects" + (id?.let { "/$it" } ?: "")
    val project: ProjectResponse = decode(request(if (id == null) "POST" else "PUT", path, body))
    refresh()
    return project
}

suspend fun SpindleRepository.setProjectArchived(id: String, archived: Boolean) {
    request("POST", "${workspacePath()}/projects/$id/${if (archived) "archive" else "restore"}"); refresh()
}

suspend fun SpindleRepository.deleteProject(id: String) { request("DELETE", "${workspacePath()}/projects/$id"); refresh() }

suspend fun SpindleRepository.saveTask(projectId: String, name: String, estimateSeconds: Long? = null, isDone: Boolean = false, id: String? = null) {
    val path = "${workspacePath()}/projects/$projectId/tasks" + (id?.let { "/$it" } ?: "")
    request(if (id == null) "POST" else "PUT", path, buildJsonObject {
        put("name", name); put("estimateSeconds", estimateSeconds?.let(::JsonPrimitive) ?: JsonNull); put("isDone", isDone)
    })
    refresh()
}

suspend fun SpindleRepository.deleteTask(projectId: String, id: String) { request("DELETE", "${workspacePath()}/projects/$projectId/tasks/$id"); refresh() }

suspend fun SpindleRepository.saveClient(name: String, id: String? = null): ClientResponse {
    val path = "${workspacePath()}/clients" + (id?.let { "/$it" } ?: "")
    val client: ClientResponse = decode(request(if (id == null) "POST" else "PUT", path, buildJsonObject { put("name", name) }))
    refresh()
    return client
}

suspend fun SpindleRepository.setClientArchived(id: String, archived: Boolean) {
    request("POST", "${workspacePath()}/clients/$id/${if (archived) "archive" else "restore"}"); refresh()
}

suspend fun SpindleRepository.deleteClient(id: String) { request("DELETE", "${workspacePath()}/clients/$id"); refresh() }

suspend fun SpindleRepository.saveTag(name: String, id: String? = null): TagResponse {
    val path = "${workspacePath()}/tags" + (id?.let { "/$it" } ?: "")
    val tag: TagResponse = decode(request(if (id == null) "POST" else "PUT", path, buildJsonObject { put("name", name) }))
    refresh()
    return tag
}

suspend fun SpindleRepository.deleteTag(id: String) { request("DELETE", "${workspacePath()}/tags/$id"); refresh() }

// MARK: - Reports

suspend fun SpindleRepository.summaryReport(body: JsonObject): SummaryReport = decode(request("POST", "${workspacePath()}/reports/summary", body))
suspend fun SpindleRepository.detailedReport(body: JsonObject): DetailedReport = decode(request("POST", "${workspacePath()}/reports/detailed", body))
suspend fun SpindleRepository.weeklyReport(body: JsonObject): WeeklyReport = decode(request("POST", "${workspacePath()}/reports/weekly", body))
suspend fun SpindleRepository.savedReports(): List<SavedReportResponse> = decode(request("GET", "${workspacePath()}/reports/saved"))
suspend fun SpindleRepository.members(): List<MemberResponse> = decode(request("GET", "${workspacePath()}/members"))
suspend fun SpindleRepository.groups(): List<GroupResponse> = decode(request("GET", "${workspacePath()}/groups"))

suspend fun SpindleRepository.saveReport(name: String, kind: String, parameters: String, id: String?): SavedReportResponse =
    decode(request(if (id == null) "POST" else "PUT", "${workspacePath()}/reports/saved" + (id?.let { "/$it" } ?: ""), buildJsonObject {
        put("name", name); put("kind", kind); put("parameters", parameters)
    }))

suspend fun SpindleRepository.deleteSavedReport(id: String) { request("DELETE", "${workspacePath()}/reports/saved/$id") }

suspend fun SpindleRepository.exportReport(kind: String, format: String, body: JsonObject): ExportFile =
    download("POST", "${workspacePath()}/reports/$kind/export?format=$format", body)

// MARK: - Account

/**
 * Where the organization's trial or plan stands. Only its owners and admins may ask, and only on the hosted service:
 * anyone else gets a 403 or 404.
 */
suspend fun SpindleRepository.subscription(organizationId: String): SubscriptionResponse =
    decode(request("GET", "/api/organizations/$organizationId/subscription"))

suspend fun SpindleRepository.sessions(): List<SessionResponse> = decode(request("GET", "/api/me/sessions"))
suspend fun SpindleRepository.revokeSession(id: String) { request("DELETE", "/api/me/sessions/$id") }
suspend fun SpindleRepository.revokeOtherSessions() { request("POST", "/api/me/sessions/revoke-others") }

suspend fun SpindleRepository.updateProfile(fullName: String, timeZone: String) {
    request("PUT", "/api/me/profile", buildJsonObject { put("fullName", fullName); put("timeZone", timeZone) }); refresh()
}

suspend fun SpindleRepository.updatePreferences(preferences: PreferencesResponse) {
    request("PUT", "/api/me/preferences", json.encodeToJsonElement(preferences).jsonObject); refresh()
}

suspend fun SpindleRepository.changePassword(current: String, new: String) {
    request("POST", "/api/me/password", buildJsonObject { put("currentPassword", current); put("newPassword", new) })
}

suspend fun SpindleRepository.createWorkspace(name: String) {
    val created = request("POST", "/api/workspaces", buildJsonObject { put("name", name) })
    refresh()
    (created as? JsonObject)?.get("id")?.jsonPrimitive?.contentOrNull?.let { switchWorkspace(it) }
}

suspend fun SpindleRepository.sendConfirmationEmail() { request("POST", "/api/me/confirmation-email") }
suspend fun SpindleRepository.exportData(): ExportFile = download("GET", "/api/me/export")
/** Without a password for an account made with Google or Apple: the server then wants a sign-in on this phone just now. */
suspend fun SpindleRepository.deleteAccount(password: String?) {
    request("POST", "/api/me/delete", buildJsonObject { password?.let { put("password", it) } })
    signOut(force = true)
}
