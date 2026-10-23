package com.spindletrack.android.ui

import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.graphics.Color
import com.spindletrack.android.data.*
import kotlinx.coroutines.delay
import java.text.NumberFormat
import java.time.*
import java.time.format.DateTimeFormatter
import java.time.temporal.ChronoUnit
import java.util.Currency
import java.util.Locale
import kotlin.math.ceil
import kotlin.math.max
import kotlin.math.roundToInt
import kotlin.math.roundToLong

// What the screens share: the account's way of showing time, an entry's parts, and the workspace's rules, as the
// iPhone app's SpindleKit has them, so both apps say the same thing the same way.

/** Durations as Spindle shows them, the same as the web and iPhone apps. */
object Durations {
    /** `h:mm:ss`: 0:05:09, 12:30:00. A running timer always shows this. */
    fun clock(totalSeconds: Long): String {
        val seconds = max(0, totalSeconds)
        return "%d:%02d:%02d".format(Locale.ROOT, seconds / 3600, seconds / 60 % 60, seconds % 60)
    }

    /** A finished duration in the person's format: `hms`, `hm` or `decimal`. */
    fun format(totalSeconds: Long, format: String): String {
        val seconds = max(0, totalSeconds)
        return when (format) {
            "hm" -> "%d:%02d".format(Locale.ROOT, seconds / 3600, seconds / 60 % 60)
            "decimal" -> "%.2f h".format(Locale.ROOT, seconds / 3600.0)
            else -> clock(seconds)
        }
    }

    /** `12h 05m`, `45m`, for totals and estimates. */
    fun hoursMinutes(totalSeconds: Long): String {
        val minutes = max(0, totalSeconds) / 60
        return if (minutes >= 60) "%dh %02dm".format(Locale.ROOT, minutes / 60, minutes % 60) else "${minutes}m"
    }
}

/** Dates and times as the account shows them: in its time zone and with its clock, whatever the phone is set to. */
data class AccountTime(val zone: ZoneId, val hour12: Boolean, val durationFormat: String, val weekStart: Int) {
    private val locale = Locale.ENGLISH

    fun day(instant: Instant): LocalDate = instant.atZone(zone).toLocalDate()
    fun today(): LocalDate = LocalDate.now(zone)
    fun start(day: LocalDate): Instant = day.atStartOfDay(zone).toInstant()

    fun clock(instant: Instant): String = format(instant, if (hour12) "h:mm a" else "HH:mm")
    fun duration(seconds: Long, running: Boolean = false): String =
        if (running) Durations.clock(seconds) else Durations.format(seconds, durationFormat)

    /** "Today", "Yesterday", "Fri, 9 Oct" or with the year when it isn't this one. */
    fun dayHeading(day: LocalDate, today: LocalDate = today()): String = when (day) {
        today -> "Today"
        today.minusDays(1) -> "Yesterday"
        else -> day.format(DateTimeFormatter.ofPattern(if (day.year == today.year) "EEE, d MMM" else "EEE, d MMM yyyy", locale))
    }

    fun longDay(day: LocalDate): String = day.format(DateTimeFormatter.ofPattern("d MMMM yyyy", locale))
    fun shortDay(day: LocalDate): String = day.format(DateTimeFormatter.ofPattern("d MMM yyyy", locale))
    fun pattern(day: LocalDate, pattern: String): String = day.format(DateTimeFormatter.ofPattern(pattern, locale))

    /** The week a day is in, from the account's first day of the week (0 is Sunday). */
    fun week(day: LocalDate): List<LocalDate> {
        val back = (day.dayOfWeek.value % 7 - weekStart + 7) % 7
        val first = day.minusDays(back.toLong())
        return (0L..6L).map { first.plusDays(it) }
    }

    private fun format(instant: Instant, pattern: String): String =
        instant.atZone(zone).format(DateTimeFormatter.ofPattern(pattern, locale)).replace("AM", "am").replace("PM", "pm")
}

fun AppState.time(): AccountTime {
    val zone = runCatching { ZoneId.of(me?.timeZone) }.getOrDefault(ZoneId.systemDefault())
    val preferences = me?.preferences
    return AccountTime(zone, preferences?.timeFormat == "12h", preferences?.durationFormat?.ifBlank { "hms" } ?: "hms", preferences?.weekStart ?: 1)
}

/** Kept for the rules that predate AccountTime. */
fun appZone(state: AppState): ZoneId = state.time().zone

// MARK: - Entries

val TimeEntryResponse.startInstant: Instant get() = runCatching { Instant.parse(start) }.getOrDefault(Instant.EPOCH)
val TimeEntryResponse.stopInstant: Instant? get() = stop?.let { runCatching { Instant.parse(it) }.getOrNull() }
val TimeEntryResponse.isRunning: Boolean get() = stop == null
val TimeEntryResponse.isSplit: Boolean get() = shares.size > 1

/** The parts of an entry: a split's shares, or the whole entry on its one project. */
val TimeEntryResponse.parts: List<TimeShareResponse>
    get() = if (isSplit) shares else listOf(TimeShareResponse(projectId, taskId, isBillable, 100))

fun TimeEntryResponse.seconds(now: Instant = Instant.now()): Long =
    max(0, Duration.between(startInstant, stopInstant ?: now).seconds)

fun entrySeconds(entry: TimeEntryResponse, now: Instant = Instant.now()): Long = entry.seconds(now)

/** Day entries include entries crossing midnight, clipped only for displayed day totals. */
fun daySeconds(entry: TimeEntryResponse, day: LocalDate, zone: ZoneId, now: Instant = Instant.now()): Long {
    val start = maxOf(entry.startInstant, day.atStartOfDay(zone).toInstant())
    val stop = minOf(entry.stopInstant ?: now, day.plusDays(1).atStartOfDay(zone).toInstant())
    return Duration.between(start, stop).seconds.coerceAtLeast(0)
}

/** Whether each part of the split has a description of its own; the entry's description is then made of them. */
val TimeEntryResponse.describesParts: Boolean get() = describesEach(shares)

fun TimeShareResponse.toRequest() = TimeShareRequest(projectId = projectId, taskId = taskId, isBillable = isBillable, percent = percent, description = description)

/** An entry's details as a draft to save, e.g. to change one of them. */
fun TimeEntryResponse.draft() = EntryDraft(
    description = description, projectId = projectId, taskId = taskId, tagIds = tagIds, isBillable = isBillable,
    shares = shares.takeIf { it.size > 1 }?.map { it.toRequest() }, start = start, stop = stop,
)

/** The clock, ticking each second while something shows a running time. */
@Composable
fun clockNow(ticking: Boolean = true, every: Long = 1000): Instant {
    var now by remember { mutableStateOf(Instant.now()) }
    LaunchedEffect(ticking, every) {
        now = Instant.now()
        while (ticking) {
            delay(every - System.currentTimeMillis() % every)
            now = Instant.now()
        }
    }
    return now
}

// MARK: - Catalog

fun AppState.project(id: String?): ProjectResponse? = id?.let { projectId -> projects.find { it.id == projectId } }
fun AppState.task(id: String?): TaskResponse? = id?.let { taskId -> projects.firstNotNullOfOrNull { project -> project.tasks.find { it.id == taskId } } }
fun AppState.client(id: String?): ClientResponse? = id?.let { clientId -> clients.find { it.id == clientId } }
fun AppState.tag(id: String): TagResponse? = tags.find { it.id == id }
val AppState.sortedTags: List<TagResponse> get() = tags.sortedBy { it.name.lowercase() }

/** Projects by client, the clients in order and those without one last; archived ones only when kept. */
fun AppState.projectGroups(keeping: String? = null): List<Pair<ClientResponse?, List<ProjectResponse>>> {
    val open = projects.filter { it.archivedAt == null || it.id == keeping }.sortedBy { it.name.lowercase() }
    val byClient = open.groupBy { it.clientId }
    val named = clients.sortedBy { it.name.lowercase() }.mapNotNull { client -> byClient[client.id]?.let { client to it } }
    val known = clients.map { it.id }.toSet()
    val without = open.filter { it.clientId == null || it.clientId !in known }
    return named + listOfNotNull(without.takeIf { it.isNotEmpty() }?.let { null to it })
}

fun AppState.colorOf(part: TimeShareResponse): Color? = project(part.projectId)?.let { projectColor(it.color) }
fun AppState.nameOf(part: TimeShareResponse): String {
    val project = project(part.projectId) ?: return "No project"
    return task(part.taskId)?.let { "${project.name} · ${it.name}" } ?: project.name
}

/** "Website 60%, Research 40%", for labels read aloud. */
fun AppState.describe(parts: List<TimeShareResponse>): String = parts.joinToString(", ") { "${nameOf(it)} ${it.percent}%" }

// MARK: - Rules

val AppState.isAdmin: Boolean get() = me?.workspaces?.find { it.id == workspaceId }?.role.equals("admin", ignoreCase = true)
fun workspaceAdmin(state: AppState): Boolean = state.isAdmin
val AppState.canCreateProjects: Boolean get() = isAdmin || settings?.onlyAdminsCreateProjects == false
val AppState.canCreateTags: Boolean get() = isAdmin || settings?.onlyAdminsCreateTags == false
/** As the server says; a server from before it said nothing let owners and admins of any organization create them. */
val AppState.canCreateWorkspaces: Boolean get() {
    val me = me ?: return false
    return me.newWorkspace?.allowed ?: me.organizations.any { it.role == "owner" || it.role == "admin" }
}

/** Says what a new workspace is and where it goes, before the person creates it. */
val AppState.newWorkspaceDescription: String get() {
    val space = "its own projects, clients, tags and team"
    val target = me?.newWorkspace
    val organizationId = me?.workspaces?.find { it.id == workspaceId }?.organizationId
    val current = me?.organizations?.find { it.id == organizationId }?.name
    if (target != null && target.allowed && target.organizationId == null) {
        val apart = if (current != null) "A space of your own, apart from $current, with $space." else "A space of your own, with $space."
        val days = target.trialDays ?: return "$apart You own it and can invite people to it."
        return "$apart ${if (current != null) "It's not on $current's plan: it" else "It"} starts its own $days-day free trial."
    }
    val elsewhere = target?.organizationId?.takeIf { it != organizationId }?.let { id -> me?.organizations?.find { it.id == id }?.name }
    return if (elsewhere != null) "A separate space in $elsewhere, with $space. You become its admin." else "A separate space with $space. You become its admin."
}
val AppState.lockedBefore: LocalDate? get() = settings?.lockedBefore?.let { runCatching { LocalDate.parse(it) }.getOrNull() }

/** Whether a day is locked for this person: before the workspace's lock date, unless they are an admin. */
fun AppState.isLocked(day: LocalDate): Boolean = !isAdmin && lockedBefore?.let { day < it } == true
fun AppState.isLocked(instant: Instant): Boolean = isLocked(time().day(instant))

fun entryLocked(entry: TimeEntryResponse, state: AppState): Boolean =
    runCatching { state.isLocked(Instant.parse(entry.start)) }.getOrDefault(false)

/** "Entries before 1 October 2026 are locked." */
fun AppState.lockMessage(): String = lockedBefore?.let { "Entries before ${time().longDay(it)} are locked, so this one can't be changed." }
    ?: "This entry is locked."

/** Whether the workspace asks for a task on this project: only while it has open tasks. */
fun AppState.requiresTask(project: ProjectResponse?): Boolean =
    settings?.requireTask == true && project?.tasks?.any { !it.isDone } == true

const val DescriptionRequired = "Add a description."

/** The details a finished entry still needs, by field, with what to tell the person. */
fun AppState.missingDetails(draft: EntryDraft): Map<String, String> {
    val rules = settings ?: return emptyMap()
    val missing = linkedMapOf<String, String>()
    val split = draft.shares?.takeIf { it.size > 1 }?.map { TimeShareResponse(it.projectId, it.taskId, it.isBillable ?: false, it.percent, it.description) }
    // Parts described one by one each need their own.
    if (rules.requireDescription && (if (describesEach(split)) split!!.any { it.description.isNullOrBlank() } else draft.description.isBlank())) {
        missing["description"] = DescriptionRequired
    }
    val parts = split ?: listOf(TimeShareResponse(draft.projectId, draft.taskId, draft.isBillable, 100))
    if (rules.requireProject && parts.any { it.projectId == null }) missing["projectId"] = "Choose a project."
    else if (parts.any { it.taskId == null && requiresTask(project(it.projectId)) }) missing["taskId"] = "Choose a task."
    if (rules.requireTag && draft.tagIds.isEmpty()) missing["tagIds"] = "Choose at least one tag."
    return missing
}

fun missingDetails(entry: TimeEntryResponse, state: AppState): Boolean = state.missingDetails(entry.draft()).isNotEmpty()

/** The first day new time may go on, when some are locked. */
val AppState.firstOpenDay: LocalDate? get() = if (isAdmin) null else lockedBefore

// MARK: - Colours and money

/** The colours offered for projects, as on the web and iPhone apps. */
val ProjectColors = listOf(
    "#0B83D9", "#9E5BD9", "#D94182", "#E36A00", "#BF7000", "#2DA608", "#06A893", "#C9806B",
    "#465BB3", "#990099", "#C7AF14", "#566614", "#D92B2B", "#525266", "#991102",
)

fun projectColor(hex: String?): Color {
    val digits = hex?.removePrefix("#")?.takeIf { it.length == 6 } ?: return Color(0xFF8E8E93)
    return runCatching { Color(0xFF000000 or digits.toLong(16)) }.getOrDefault(Color(0xFF8E8E93))
}

/** A project's colour for text on its tint: drawn a quarter of the way towards the text colour. */
fun readableColor(hex: String?, dark: Boolean): Color? {
    if (hex == null) return null
    val base = projectColor(hex)
    val target = if (dark) 0.95f else 0.08f
    fun channel(value: Float) = value * 0.75f + target * 0.25f
    return Color(channel(base.red), channel(base.green), channel(base.blue))
}

object ReportFormat {
    /** Round hour marks for a chart's axis that reach at least `maxHours`: about four steps of a friendly size. */
    fun hourTicks(maxHours: Double): List<Double> {
        val steps = listOf(0.25, 0.5, 1.0, 2.0, 3.0, 4.0, 5.0, 6.0, 8.0, 10.0, 12.0, 15.0, 20.0, 25.0, 30.0, 40.0, 50.0, 60.0, 80.0, 100.0, 150.0, 200.0, 250.0, 300.0, 400.0, 500.0, 750.0, 1000.0, 1500.0, 2000.0, 2500.0, 5000.0)
        val step = steps.firstOrNull { it * 4 >= maxHours } ?: (ceil(maxHours / 4 / 1000) * 1000)
        val count = max(1, ceil(maxHours / step).toInt())
        return (0..count).map { it * step }
    }

    /** An axis label for a number of hours: 30m, 1h 30m, 8h. */
    fun hourTick(hours: Double): String {
        if (hours == 0.0) return "0"
        val whole = hours.toInt()
        val minutes = ((hours - whole) * 60).roundToInt()
        if (whole == 0) return "${minutes}m"
        return if (minutes > 0) "${whole}h ${minutes}m" else "${whole}h"
    }

    /** The share of a whole as a rounded percentage, e.g. 33%, 3.3%. */
    fun percent(part: Double, whole: Double): String {
        if (whole <= 0) return "0%"
        val value = part / whole * 100
        return if (value >= 10 || value == 0.0) "${value.roundToLong()}%" else "%.1f%%".format(Locale.ROOT, value)
    }

    /** An amount in the workspace's currency, or a dash where there is none. */
    fun money(amount: Double?, currency: String): String {
        if (amount == null) return "—"
        val format = NumberFormat.getCurrencyInstance(Locale.ENGLISH)
        runCatching { format.currency = Currency.getInstance(currency.ifBlank { "EUR" }) }
        return format.format(amount)
    }

    /** A group keeps its project's colour; the rest get one from the palette by position, and "without" none. */
    fun colorOf(key: String, color: String?, index: Int): String? = color ?: if (key == "none") null else ProjectColors[index % ProjectColors.size]
}

/** Whole days between two dates, for ranges. */
fun daysBetween(from: LocalDate, to: LocalDate): Long = ChronoUnit.DAYS.between(from, to)

// MARK: - Plan

/** The current workspace's organization, while it is read-only: its trial or plan has ended, so nothing can change. */
val AppState.readOnlyOrganization: OrganizationSummary?
    get() {
        val organizationId = me?.workspaces?.find { it.id == workspaceId }?.organizationId ?: return null
        return me.organizations.find { it.id == organizationId && it.access == "readOnly" }
    }

/**
 * The current workspace's organization, when the person may see its trial or plan: they own or administer it, on
 * the hosted service. Self-hosted servers have no plans.
 */
val AppState.planOrganization: OrganizationSummary?
    get() {
        if (serverInfo?.hosted != true) return null
        val organizationId = me?.workspaces?.find { it.id == workspaceId }?.organizationId ?: return null
        return me.organizations.find { it.id == organizationId && it.role != "member" }
    }

/** The plan's state in a word or two, for its row in Settings, from what `/me` already says. */
fun OrganizationSummary.planSummary(now: Instant = Instant.now()): String? {
    if (access == "readOnly") return "Ended"
    val end = trialEndsAt?.let { runCatching { Instant.parse(it) }.getOrNull() } ?: return null
    return PlanDescription.describeDaysLeft(PlanDescription.daysLeft(end, now))
}

/**
 * What the apps say about an organization's trial or plan, in the same words as the web's plan page and the iPhone.
 * It names no way to pay: like the iPhone app, the Android app doesn't point to buying outside its store.
 */
data class PlanDescription(val badge: String, val tone: Tone, val title: String, val text: String) {
    enum class Tone { Good, Neutral, Bad }

    companion object {
        /** Describes a subscription. The end of a trial or paid period is shown as its last day, in the person's time. */
        fun of(subscription: SubscriptionResponse, organizationName: String, time: AccountTime, now: Instant = Instant.now()): PlanDescription {
            fun instant(value: String?) = value?.let { runCatching { Instant.parse(it) }.getOrNull() }
            fun lastDay(end: Instant) = time.longDay(time.day(end.minusSeconds(1)))
            val trialEndsAt = instant(subscription.trialEndsAt)
            val paidUntil = instant(subscription.paidUntil)
            val readOnly = subscription.access == "readOnly"
            val readOnlyText = "$organizationName is read-only: everything is still here and can be read, reported and exported, but no time can be added or changed."
            val canceled = { end: Instant -> PlanDescription("Canceled", Tone.Neutral, "Works until ${lastDay(end)}", "The plan won’t renew.") }

            return when (subscription.status) {
                "trialing" -> when {
                    subscription.hasPlan -> {
                        val firstCharge = instant(subscription.nextChargeAt) ?: trialEndsAt
                        PlanDescription("Plan chosen", Tone.Good, firstCharge?.let { "Starts after ${lastDay(it)}" } ?: "Plan chosen",
                            "The trial goes on until then, with nothing charged. The first charge comes when it ends, for the seats there are then.")
                    }
                    trialEndsAt != null && !readOnly -> PlanDescription("Free trial", Tone.Neutral, describeDaysLeft(daysLeft(trialEndsAt, now)),
                        "The trial lasts until the end of ${lastDay(trialEndsAt)}, with everything working.")
                    else -> PlanDescription("Trial ended", Tone.Bad, "The trial has ended",
                        trialEndsAt?.let { "Its last day was ${lastDay(it)}. $readOnlyText" } ?: readOnlyText)
                }
                "active" ->
                    if (subscription.cancelsAtPeriodEnd && paidUntil != null) canceled(paidUntil)
                    else PlanDescription("Paid", Tone.Good, paidUntil?.let { "Paid until ${lastDay(it)}" } ?: "Paid",
                        "The plan renews by itself, and its seats follow the people in the organization.")
                "pastDue" -> PlanDescription("Payment failed", Tone.Bad, "A payment didn’t go through",
                    "It is being tried again, and everything keeps working meanwhile.")
                "canceled" ->
                    if (paidUntil != null && !readOnly) canceled(paidUntil)
                    else PlanDescription("Ended", Tone.Bad, "The plan has ended", readOnlyText)
                "comped" -> PlanDescription("Free", Tone.Good, "Free, on us", "$organizationName gets everything in Spindle at no charge.")
                // A state this version doesn't know yet: say only whether it works.
                else -> if (readOnly) PlanDescription("Ended", Tone.Bad, "The plan has ended", readOnlyText)
                    else PlanDescription("Active", Tone.Good, "Everything works", "")
            }
        }

        /** Whole days until an end, counting a part of a day as one; 0 once it has passed. */
        fun daysLeft(end: Instant, now: Instant = Instant.now()): Int =
            max(0, ceil(Duration.between(now, end).toMillis() / 86_400_000.0).toInt())

        /** "12 days left", or "Last day" when less than a day is left. */
        fun describeDaysLeft(days: Int): String = if (days <= 1) "Last day" else "$days days left"
    }
}
