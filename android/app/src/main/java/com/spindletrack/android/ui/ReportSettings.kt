package com.spindletrack.android.ui

import java.time.LocalDate
import kotlinx.serialization.json.*

internal fun reportRange(period: String, from: String, to: String, today: LocalDate, weekStart: Int, weekly: Boolean): Pair<LocalDate,LocalDate> {
    fun week(day: LocalDate): Pair<LocalDate,LocalDate> { val start = day.minusDays(((day.dayOfWeek.value % 7 - weekStart + 7) % 7).toLong()); return start to start.plusDays(6) }
    val range = when(period) {
        "today" -> today to today
        "yesterday" -> today.minusDays(1).let { it to it }
        "lastWeek" -> week(today.minusDays(7))
        "thisMonth" -> today.withDayOfMonth(1) to today.withDayOfMonth(today.lengthOfMonth())
        "lastMonth" -> today.minusMonths(1).let { it.withDayOfMonth(1) to it.withDayOfMonth(it.lengthOfMonth()) }
        "thisYear" -> today.withDayOfYear(1) to today.withMonth(12).withDayOfMonth(31)
        "lastYear" -> today.minusYears(1).let { it.withDayOfYear(1) to it.withMonth(12).withDayOfMonth(31) }
        "custom" -> LocalDate.parse(from) to LocalDate.parse(to)
        else -> week(today)
    }
    require(range.first <= range.second) { "The start date must be before the end date." }
    return if(weekly) week(if(period == "custom") range.first else minOf(range.second,today)) else range
}
/** Saved reports use the same optional settings keys as web and iOS; invalid values fall back to defaults. */
internal fun readReportSettings(parameters: String, kind: String): JsonObject {
    val input = runCatching { Json.parseToJsonElement(parameters).jsonObject }.getOrDefault(JsonObject(emptyMap()))
    return buildJsonObject {
        fun choice(key: String, allowed: Set<String>) { (input[key] as? JsonPrimitive)?.contentOrNull?.takeIf { it in allowed }?.let { put(key,it) } }
        choice("tab",setOf("summary","detailed","weekly"))
        if((input["tab"] as? JsonPrimitive)?.contentOrNull !in setOf("summary","detailed","weekly")) put("tab",kind.takeIf { it in setOf("summary","detailed","weekly") } ?: "summary")
        choice("period",setOf("today","yesterday","thisWeek","lastWeek","thisMonth","lastMonth","thisYear","lastYear","custom"))
        listOf("from","to").forEach { key -> (input[key] as? JsonPrimitive)?.contentOrNull?.takeIf { it.matches(Regex("[0-9]{4}-[0-9]{2}-[0-9]{2}")) && runCatching { LocalDate.parse(it) }.isSuccess }?.let { put(key,it) } }
        val idPattern = Regex("[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")
        listOf("users","groups","projects","clients","tags").forEach { key ->
            val ids = (input[key] as? JsonArray)?.mapNotNull { (it as? JsonPrimitive)?.contentOrNull?.takeIf(idPattern::matches) }?.distinct().orEmpty()
            if(ids.isNotEmpty()) put(key,JsonArray(ids.map(::JsonPrimitive)))
        }
        choice("billable",setOf("yes","no")); choice("groupBy",setOf("project","client","user","tag","task","description")); choice("subGroupBy",setOf("none","project","client","user","tag","task","description")); choice("sort",setOf("date","duration","description","user","project")); choice("rounding",setOf("none","up","nearest","down"))
        (input["q"] as? JsonPrimitive)?.contentOrNull?.takeIf { it.isNotBlank() }?.let { put("q",it.take(200)) }
        if((input["asc"] as? JsonPrimitive)?.booleanOrNull == true) put("asc",true)
        (input["roundTo"] as? JsonPrimitive)?.intOrNull?.takeIf { it in listOf(1,5,6,10,12,15,30,60) }?.let { put("roundTo",it) }
        // Pagination is deliberately absent: opening a saved report starts at its first page.
    }
}

internal fun JsonElement.obj(): JsonObject = this as? JsonObject ?: JsonObject(emptyMap())
internal fun JsonObject.str(key: String, fallback: String = ""): String = (get(key) as? JsonPrimitive)?.contentOrNull ?: fallback
internal fun JsonObject.bool(key: String): Boolean = (get(key) as? JsonPrimitive)?.booleanOrNull ?: false
internal fun JsonObject.num(key: String): Long = (get(key) as? JsonPrimitive)?.longOrNull ?: 0L
internal fun JsonObject.ids(key: String): List<String> = (get(key) as? JsonArray)?.mapNotNull { (it as? JsonPrimitive)?.contentOrNull }.orEmpty()

/** The settings with one changed, or removed when `value` is null. */
internal fun JsonObject.with(key: String, value: JsonElement?): JsonObject =
    JsonObject(toMutableMap().apply { if (value == null) remove(key) else put(key, value) })

internal fun JsonObject.with(key: String, value: String?): JsonObject = with(key, value?.let(::JsonPrimitive))

private val filterKeys = listOf("users", "groups", "projects", "clients", "tags", "billable", "q")

/** Whether the report is narrowed by who, what or which billing. */
internal val JsonObject.hasFilters: Boolean get() = filterKeys.any { containsKey(it) }
internal val JsonObject.withoutFilters: JsonObject get() = JsonObject(filterKeys.fold(toMutableMap()) { map, key -> map.apply { remove(key) } })

/** What the server takes for a report, from its saved settings and the days it covers. */
internal fun reportBody(settings: JsonObject, range: Pair<LocalDate, LocalDate>): JsonObject = buildJsonObject {
    val tab = settings.str("tab", "summary")
    put("from", range.first.toString()); put("to", range.second.toString())
    listOf("users" to "userIds", "groups" to "groupIds", "projects" to "projectIds", "clients" to "clientIds", "tags" to "tagIds")
        .forEach { (key, out) -> settings[key]?.let { put(out, it) } }
    settings.str("billable").takeIf { it == "yes" || it == "no" }?.let { put("billable", it == "yes") }
    settings.str("q").takeIf { it.isNotBlank() }?.let { put("description", it) }
    put("rounding", settings.str("rounding", "none")); put("roundingMinutes", settings.num("roundTo").takeIf { it > 0 } ?: 15)
    val group = settings.str("groupBy", "project")
    when (tab) {
        "detailed" -> {
            put("sort", settings.str("sort", "date")); put("descending", !settings.bool("asc"))
            put("page", settings.num("page").takeIf { it > 0 } ?: 1); put("pageSize", 50)
        }
        "weekly" -> put("groupBy", if (group == "description") "project" else group)
        else -> {
            put("groupBy", group)
            val sub = settings.str("subGroupBy", if (group == "description") "none" else "description")
            put("subGroupBy", if (sub == "none" || sub == group) JsonNull else JsonPrimitive(sub))
        }
    }
}

/** "14 – 20 Sep 2026", "28 Sep – 4 Oct 2026", "28 Dec 2026 – 3 Jan 2027", "Wed, 30 Sep 2026". */
internal fun rangeLabel(range: Pair<LocalDate, LocalDate>): String {
    val (from, to) = range
    fun f(day: LocalDate, pattern: String) = day.format(java.time.format.DateTimeFormatter.ofPattern(pattern, java.util.Locale.ENGLISH))
    return when {
        from == to -> f(from, "EEE, d MMM yyyy")
        from.year != to.year -> "${f(from, "d MMM yyyy")} – ${f(to, "d MMM yyyy")}"
        else -> "${f(from, if (from.month == to.month) "d" else "d MMM")} – ${f(to, "d MMM yyyy")}"
    }
}

/** The period before (-1) or after (1): months move by months and years by years; anything else by its own length. */
internal fun shiftRange(range: Pair<LocalDate, LocalDate>, direction: Int): Pair<LocalDate, LocalDate> {
    val (from, to) = range
    val next = to.plusDays(1)
    if (from.dayOfMonth == 1 && next.dayOfMonth == 1) {
        val months = (next.year - from.year) * 12L + next.monthValue - from.monthValue
        val start = from.plusMonths(months * direction)
        return start to start.plusMonths(months).minusDays(1)
    }
    val days = (java.time.temporal.ChronoUnit.DAYS.between(from, to) + 1) * direction
    return from.plusDays(days) to to.plusDays(days)
}
