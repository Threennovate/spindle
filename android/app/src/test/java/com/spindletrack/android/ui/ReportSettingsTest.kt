package com.spindletrack.android.ui

import java.time.LocalDate
import kotlinx.serialization.json.*
import org.junit.Assert.*
import org.junit.Test

class ReportSettingsTest {
    @Test fun reportWeekUsesAccountWeekStartAcrossYearBoundary() {
        val day = LocalDate.parse("2026-01-01")
        assertEquals(LocalDate.parse("2025-12-29") to LocalDate.parse("2026-01-04"),reportRange("thisWeek","","",day,1,false))
        assertEquals(LocalDate.parse("2025-12-28") to LocalDate.parse("2026-01-03"),reportRange("thisWeek","","",day,0,false))
    }
    @Test fun monthlyRangeRespectsLeapYearAndWeeklyUsesLatestBegunWeek() {
        assertEquals(LocalDate.parse("2024-02-01") to LocalDate.parse("2024-02-29"),reportRange("lastMonth","","",LocalDate.parse("2024-03-15"),1,false))
        assertEquals(LocalDate.parse("2026-09-28") to LocalDate.parse("2026-10-04"),reportRange("lastMonth","","",LocalDate.parse("2026-10-10"),1,true))
        assertEquals(LocalDate.parse("2026-10-05") to LocalDate.parse("2026-10-11"),reportRange("custom","2026-10-09","2026-10-30",LocalDate.parse("2026-10-10"),1,true))
    }
    @Test(expected = IllegalArgumentException::class) fun reversedCustomRangeIsRejected() {
        reportRange("custom","2026-10-11","2026-10-10",LocalDate.parse("2026-10-10"),1,false)
    }
    @Test fun savedIosAndWebSettingsKeepSharedKeysAndResetPagination() {
        val parameters = """{"tab":"detailed","period":"custom","from":"2026-10-01","to":"2026-10-10","users":["00000000-0000-0000-0000-000000000001"],"projects":["00000000-0000-0000-0000-000000000000"],"billable":"yes","q":"Review","groupBy":"project","subGroupBy":"none","sort":"duration","asc":true,"rounding":"nearest","roundTo":15,"page":4}"""
        val settings = readReportSettings(parameters,"detailed")
        assertEquals("detailed",settings["tab"]!!.jsonPrimitive.content)
        assertEquals("yes",settings["billable"]!!.jsonPrimitive.content)
        assertEquals("none",settings["subGroupBy"]!!.jsonPrimitive.content)
        assertEquals(15,settings["roundTo"]!!.jsonPrimitive.int)
        assertTrue(settings.containsKey("projects"))
        assertFalse(settings.containsKey("projectIds"))
        assertFalse(settings.containsKey("page"))
        assertEquals("2026-10-01",settings["from"]!!.jsonPrimitive.content)
    }
    @Test fun corruptedSavedSettingsDropInvalidTypesAndValues() {
        val settings = readReportSettings("""{"tab":"bad","from":"2026-02-30","to":["wrong"],"roundTo":7,"billable":false,"sort":{},"tags":[null,"invalid","00000000-0000-0000-0000-000000000001","00000000-0000-0000-0000-000000000001"],"q":"  "}""","weekly")
        assertEquals("weekly",settings["tab"]!!.jsonPrimitive.content)
        assertEquals(1,settings["tags"]!!.jsonArray.size)
        listOf("from","to","roundTo","billable","sort","q").forEach { assertFalse(it,settings.containsKey(it)) }
    }
}
