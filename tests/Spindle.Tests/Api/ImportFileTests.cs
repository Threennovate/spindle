using NodaTime;
using Spindle.Api.Import;

namespace Spindle.Tests.Api;

public sealed class ImportFileTests
{
    [Fact]
    public void Reads_a_Toggl_Track_detailed_report()
    {
        const string csv = """
            User,Email,Client,Project,Task,Description,Billable,Start date,Start time,End date,End time,Duration,Tags,Amount (EUR)
            Ana Anić,Ana@Example.com,Acme,Website,Wireframes,Homepage layout,Yes,2026-09-14,09:00:00,2026-09-14,11:30:00,02:30:00,"design, client",250.00
            Ana Anić,ana@example.com,,,,Email,No,2026-09-14,23:30:00,2026-09-15,00:15:00,00:45:00,,
            """;

        var file = ImportFile.Parse(csv, DateOrder.Auto);

        Assert.Equal(("Toggl Track", DateOrder.YearMonthDay, false), (file.Format, file.DateOrder, file.DateOrderGuessed));
        Assert.Empty(file.Problems);
        var first = file.Rows[0];
        Assert.Equal(("Ana Anić", "ana@example.com", "Acme", "Website", "Wireframes", "Homepage layout", true), (first.UserName, first.Email, first.Client, first.Project, first.Task, first.Description, first.Billable));
        Assert.Equal((new LocalDateTime(2026, 9, 14, 9, 0), new LocalDateTime(2026, 9, 14, 11, 30)), (first.Start, first.End));
        Assert.Equal(["design", "client"], first.Tags);
        Assert.Equal((new LocalDateTime(2026, 9, 14, 23, 30), new LocalDateTime(2026, 9, 15, 0, 15), false), (file.Rows[1].Start, file.Rows[1].End, file.Rows[1].Billable));
        Assert.Null(file.Rows[1].Project);
    }

    [Fact]
    public void Reads_a_Clockify_detailed_report_with_US_dates()
    {
        const string csv = """
            Project,Client,Description,Task,User,Group,Email,Tags,Billable,Start Date,Start Time,End Date,End Time,Duration (h),Duration (decimal),Billable Rate (USD),Billable Amount (USD)
            Website,Acme,Standup,,Ivo Ivić,,ivo@example.com,meeting,Yes,09/14/2026,09:00:00 AM,09/14/2026,09:15:00 AM,00:15:00,0.25,80.00,20.00
            Website,Acme,Review,,Ivo Ivić,,ivo@example.com,,No,09/15/2026,01:30:00 PM,09/15/2026,03:00:00 PM,01:30:00,1.50,0.00,0.00
            """;

        var file = ImportFile.Parse(csv, DateOrder.Auto);

        Assert.Equal(("Clockify", DateOrder.MonthDayYear, false), (file.Format, file.DateOrder, file.DateOrderGuessed));
        Assert.Equal(new LocalDateTime(2026, 9, 15, 13, 30), file.Rows[1].Start);
        Assert.Equal(new LocalDateTime(2026, 9, 15, 15, 0), file.Rows[1].End);
        Assert.Equal(["meeting"], file.Rows[0].Tags);
    }

    [Fact]
    public void Reads_Three_Tracks_own_export_and_entries_past_midnight()
    {
        const string csv = """
            Date,Start,End,Duration,Hours,Member,Project,Client,Task,Description,Tags,Billable,Amount (EUR)
            2026-09-14,23:00:00,01:00:00,2:00:00,2.00,Ana Anić,Website,Acme,,Release,,Yes,200.00
            """;

        var file = ImportFile.Parse(csv, DateOrder.Auto);

        Assert.Equal("Spindle", file.Format);
        Assert.Equal((new LocalDateTime(2026, 9, 14, 23, 0), new LocalDateTime(2026, 9, 15, 1, 0)), (file.Rows[0].Start, file.Rows[0].End));
        Assert.Equal("Ana Anić", file.Rows[0].UserName);
    }

    [Fact]
    public void Reads_a_Harvest_detailed_time_export_and_places_its_hours_on_the_day()
    {
        const string csv = """
            Date,Client,Project,Project Code,Task,Notes,Hours,Hours Rounded,Billable?,Invoiced?,Approved?,First Name,Last Name,Roles,Employee?,Billable Rate,Billable Amount,Cost Rate,Cost Amount,Currency,External Reference URL
            2026-09-14,Acme,Website,WEB,Design,"Homepage layout, second pass",1.5,1.5,Yes,No,No,Ana,Anić,Design,Yes,100,150,40,60,Euro - EUR,
            2026-09-14,,Internal,,Admin,Email,0.25,0.25,No,No,No,Ana,Anić,Design,Yes,0,0,40,10,Euro - EUR,
            2026-09-14,Acme,Website,WEB,Development,Review,2,2,Yes,No,No,Ivo,Ivić,,Yes,100,200,40,80,Euro - EUR,
            2026-09-15,Acme,Website,WEB,Design,Footer,1.1,1.25,Yes,No,No,Ana,Anić,Design,Yes,100,110,40,44,Euro - EUR,
            """;

        var file = ImportFile.Parse(csv, DateOrder.Auto);

        Assert.Equal(("Harvest", DateOrder.YearMonthDay), (file.Format, file.DateOrder));
        Assert.Empty(file.Problems);
        var first = file.Rows[0];
        Assert.Equal(("Ana Anić", null, "Acme", "Website", "Design", "Homepage layout, second pass", true), (first.UserName, first.Email, first.Client, first.Project, first.Task, first.Description, first.Billable));
        Assert.All(file.Rows, row => Assert.True(row.Placed));
        Assert.Equal(
            [
                ("Ana Anić", new LocalDateTime(2026, 9, 14, 9, 0), new LocalDateTime(2026, 9, 14, 10, 30)),
                ("Ana Anić", new LocalDateTime(2026, 9, 14, 10, 30), new LocalDateTime(2026, 9, 14, 10, 45)),
                ("Ivo Ivić", new LocalDateTime(2026, 9, 14, 9, 0), new LocalDateTime(2026, 9, 14, 11, 0)),
                ("Ana Anić", new LocalDateTime(2026, 9, 15, 9, 0), new LocalDateTime(2026, 9, 15, 10, 6)),
            ],
            file.Rows.Select(r => (r.UserName, r.Start, r.End)));
        Assert.False(file.Rows[1].Billable);
    }

    [Fact]
    public void Harvest_entries_with_start_and_end_times_keep_them()
    {
        const string csv = """
            Date,Client,Project,Task,Notes,Hours,Billable?,First Name,Last Name,Started At,Ended At
            09/14/2026,Acme,Website,Design,Timer,1.5,Yes,Ana,Anić,1:15pm,2:45pm
            09/14/2026,Acme,Website,Design,Typed in,0.5,Yes,Ana,Anić,,
            """;

        var file = ImportFile.Parse(csv, DateOrder.Auto);

        Assert.Equal(("Harvest", DateOrder.MonthDayYear), (file.Format, file.DateOrder));
        Assert.Equal((new LocalDateTime(2026, 9, 14, 13, 15), new LocalDateTime(2026, 9, 14, 14, 45), false), (file.Rows[0].Start, file.Rows[0].End, file.Rows[0].Placed));
        Assert.Equal((new LocalDateTime(2026, 9, 14, 9, 0), new LocalDateTime(2026, 9, 14, 9, 30), true), (file.Rows[1].Start, file.Rows[1].End, file.Rows[1].Placed));
    }

    [Fact]
    public void A_Harvest_day_too_full_to_start_at_nine_starts_early_enough_to_end_by_midnight()
    {
        const string csv = """
            Date,Project,Notes,Hours,First Name,Last Name
            2026-09-14,Website,Release,10,Ana,Anić
            2026-09-14,Website,Hotfix,8,Ana,Anić
            """;

        var file = ImportFile.Parse(csv, DateOrder.Auto);

        Assert.Equal(
            [(new LocalDateTime(2026, 9, 14, 6, 0), new LocalDateTime(2026, 9, 14, 16, 0)), (new LocalDateTime(2026, 9, 14, 16, 0), new LocalDateTime(2026, 9, 15, 0, 0))],
            file.Rows.Select(r => (r.Start, r.End)));
    }

    [Fact]
    public void Only_Harvest_files_may_leave_out_the_time_of_day()
    {
        var file = ImportFile.Parse("Date,Project,Hours\n2026-09-14,Website,2", DateOrder.Auto);

        Assert.Empty(file.Rows);
        Assert.Equal(1, Assert.Single(file.Problems).Line);
    }

    [Theory]
    [InlineData("14.09.2026", DateOrder.DayMonthYear, false)]
    [InlineData("14/09/2026", DateOrder.DayMonthYear, false)]
    [InlineData("09/14/2026", DateOrder.MonthDayYear, false)]
    [InlineData("03/04/2026", DateOrder.MonthDayYear, true)]
    public void Works_out_how_dates_are_written(string date, DateOrder expected, bool guessed)
    {
        var file = ImportFile.Parse($"Start date,Start time,Duration\n{date},09:00,1:00:00", DateOrder.Auto);

        Assert.Equal((expected, guessed), (file.DateOrder, file.DateOrderGuessed));
        Assert.Single(file.Rows);
    }

    [Fact]
    public void Dates_are_read_as_told_when_the_order_is_chosen()
    {
        var file = ImportFile.Parse("Start date,Start time,Duration\n03/04/2026,09:00,1.5", DateOrder.DayMonthYear);

        Assert.Equal((new LocalDateTime(2026, 4, 3, 9, 0), new LocalDateTime(2026, 4, 3, 10, 30)), (file.Rows[0].Start, file.Rows[0].End));
    }

    [Fact]
    public void Bad_rows_are_reported_by_line_and_the_rest_is_read()
    {
        const string csv = """
            Start date,Start time,End time,Description
            2026-09-14,09:00,10:00,Fine
            someday,09:00,10:00,Bad date
            2026-09-14,noon,10:00,Bad time
            2026-09-14,09:00,,No end
            """;

        var file = ImportFile.Parse(csv, DateOrder.Auto);

        Assert.Equal(DateOrder.YearMonthDay, file.DateOrder);
        Assert.Equal(["Fine"], file.Rows.Select(r => r.Description));
        Assert.Equal([3, 4, 5], file.Problems.Select(p => p.Line));
        Assert.Contains("someday", file.Problems[0].Message);
    }

    [Fact]
    public void Files_without_times_are_refused_as_a_whole()
    {
        var file = ImportFile.Parse("Project,Hours\nWebsite,2", DateOrder.Auto);

        Assert.Empty(file.Rows);
        Assert.Equal(1, Assert.Single(file.Problems).Line);
    }

    [Theory]
    [InlineData("1:30:00", 5400)]
    [InlineData("25:00:00", 90000)]
    [InlineData("1:30", 5400)]
    [InlineData("1.5", 5400)]
    [InlineData("1,25", 4500)]
    [InlineData("1.1", 3960)]
    [InlineData("0.33", 1188)]
    public void Reads_durations(string text, int seconds) => Assert.Equal(TimeSpan.FromSeconds(seconds), ImportFile.ParseDuration(text));
}
