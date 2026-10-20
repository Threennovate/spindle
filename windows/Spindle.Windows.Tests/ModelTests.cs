using Spindle.Windows.Model;

namespace Spindle.Windows.Tests;

/// <summary>The Mac's time arithmetic, case for case, so both apps read and write times alike.</summary>
public sealed class ModelTests
{
    private static readonly TimeZoneInfo Zagreb = TimeZoneInfo.FindSystemTimeZoneById("Europe/Zagreb");

    [Theory]
    [InlineData("1:30", 5400)]
    [InlineData("1:30:15", 5415)]
    [InlineData("1h 30m", 5400)]
    [InlineData("90m", 5400)]
    [InlineData("45s", 45)]
    [InlineData("2h", 7200)]
    [InlineData("1.5", 5400)]
    [InlineData("1,5", 5400)]
    [InlineData("90", 5400)]
    [InlineData(" 25min ", 1500)]
    public void DurationsReadWhatPeopleType(string text, int seconds) => Assert.Equal(seconds, Durations.Parse(text));

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("1:75")]
    public void DurationsRejectWhatIsNotOne(string text) => Assert.Null(Durations.Parse(text));

    [Fact]
    public void DurationsAreWrittenInEachFormat()
    {
        Assert.Equal("0:05:09", Durations.Clock(309.9));
        Assert.Equal("134:00:00", Durations.Clock(134 * 3600));
        Assert.Equal("1:30", Durations.Format(5400, DurationFormat.Hm));
        Assert.Equal("1.50 h", Durations.Format(5400, DurationFormat.Decimal));
        Assert.Equal("12h 05m", Durations.HoursMinutes(12 * 3600 + 300));
        Assert.Equal("45m", Durations.HoursMinutes(2700));
    }

    [Theory]
    [InlineData("9", 9, 0)]
    [InlineData("930", 9, 30)]
    [InlineData("0930", 9, 30)]
    [InlineData("2130", 21, 30)]
    [InlineData("9:5", 9, 5)]
    [InlineData("9.30", 9, 30)]
    [InlineData("9h", 9, 0)]
    [InlineData("9pm", 21, 0)]
    [InlineData("12am", 0, 0)]
    [InlineData("9:15 a.m.", 9, 15)]
    public void ClockTimesReadAsOnTheMac(string text, int hours, int minutes) => Assert.Equal(new ClockTime(hours, minutes), ClockTime.Parse(text));

    [Theory]
    [InlineData("25:00")]
    [InlineData("9:60")]
    [InlineData("13pm")]
    [InlineData("12345")]
    [InlineData("nine")]
    public void ClockTimesRejectImpossibleTimes(string text) => Assert.Null(ClockTime.Parse(text));

    [Fact]
    public void ClockFieldsReadNowAndMinutesAgoAndWriteTwelveHourClocks()
    {
        var time = new UserTime(Zagreb);
        var now = new DateTimeOffset(2026, 10, 5, 12, 30, 20, TimeSpan.Zero);
        Assert.Equal(new ClockTime(14, 30), time.ReadClockField("now", now));
        Assert.Equal(new ClockTime(14, 15), time.ReadClockField("-15", now));
        Assert.Equal(new ClockTime(13, 0), time.ReadClockField("-1:30", now));
        Assert.Equal("9:05 am", new ClockTime(9, 5).Field(hour12: true));
        Assert.Equal("12:00 pm", new ClockTime(12, 0).Field(hour12: true));
        Assert.Equal(new ClockTime(0, 10), new ClockTime(23, 55).Adding(15));
    }

    [Fact]
    public void StartFieldsAreTheLatestSuchTimeNotAfterNow()
    {
        var time = new UserTime(Zagreb);
        var now = new DateTimeOffset(2026, 10, 5, 12, 30, 20, TimeSpan.Zero);
        Assert.Equal(new DateTimeOffset(2026, 10, 5, 9, 40, 0, TimeSpan.FromHours(2)), time.ReadStartField("9:40", now));
        Assert.Equal(new DateTimeOffset(2026, 10, 4, 22, 0, 0, TimeSpan.FromHours(2)), time.ReadStartField("22:00", now));
        Assert.Equal(new DateTimeOffset(2026, 10, 5, 12, 15, 0, TimeSpan.Zero), time.ReadStartField("-15", now));
    }

    [Fact]
    public void DaysAndWeeksFollowTheAccountsZoneAndFirstWeekday()
    {
        var time = new UserTime(Zagreb, weekStart: 1);
        var now = new DateTimeOffset(2026, 10, 4, 22, 30, 0, TimeSpan.Zero);
        Assert.Equal(new DateOnly(2026, 10, 5), time.Day(now));
        Assert.Equal("Today", time.DayHeading(new DateOnly(2026, 10, 5), now));
        Assert.Equal("Yesterday", time.DayHeading(new DateOnly(2026, 10, 4), now));
        Assert.Equal("Wed, 30 Sep", time.DayHeading(new DateOnly(2026, 9, 30), now));
        Assert.Equal(new DateOnly(2026, 10, 5), time.WeekStartDay(new DateOnly(2026, 10, 11)));
        Assert.Equal(new DateOnly(2026, 10, 4), new UserTime(Zagreb, weekStart: 0).WeekStartDay(new DateOnly(2026, 10, 10)));
    }

    [Theory]
    [InlineData("30.9.2026", 2026, 9, 30)]
    [InlineData("30.9.", 2026, 9, 30)]
    [InlineData("2026-09-30", 2026, 9, 30)]
    [InlineData("7", 2026, 10, 7)]
    public void DayFieldsReadTheRegionsOrder(string text, int year, int month, int day) =>
        Assert.Equal(new DateOnly(year, month, day), UserTime.ParseDayField(text, new DateOnly(2026, 10, 5), System.Globalization.CultureInfo.GetCultureInfo("hr-HR")));

    [Fact]
    public void DayFieldsRejectDaysThatDoNotExist() =>
        Assert.Null(UserTime.ParseDayField("31.6.2026", new DateOnly(2026, 10, 5), System.Globalization.CultureInfo.GetCultureInfo("hr-HR")));

    [Fact]
    public void SplitSharesAlwaysAddUpToOneHundred()
    {
        Assert.Equal([34, 33, 33], SplitShares.Equal(3));
        Assert.Equal([60, 40], SplitShares.MoveBoundary([50, 50], 0, 58.4, 5));
        Assert.Equal([99, 1], SplitShares.MoveBoundary([50, 50], 0, 120));
        Assert.Equal([70, 15, 15], SplitShares.Setting([50, 25, 25], 0, 70));
        Assert.Equal([34, 33, 33], SplitShares.Adding([50, 50]));
        Assert.Equal([100], SplitShares.Removing([60, 40], 1));
        Assert.Equal(25, SplitShares.PercentOf(900, 3600));
        foreach (var percents in new[] { SplitShares.Setting([60, 30, 10], 2, 55), SplitShares.Removing([34, 33, 33], 0), SplitShares.Adding([70, 30]) })
            Assert.Equal(100, percents.Sum());
    }

    [Fact]
    public void PomodoroCountsIntervalsAndCountdownsAsOnTheMac()
    {
        Assert.Equal(0, Pomodoro.Intervals(24 * 60, 25));
        Assert.Equal(1, Pomodoro.Intervals(25 * 60, 25));
        Assert.Equal(2, Pomodoro.Intervals(51 * 60, 25));
        Assert.Equal(0, Pomodoro.Intervals(-5, 25));
        Assert.Equal(25 * 60, Pomodoro.FocusLeft(0, 25));
        Assert.Equal(15 * 60, Pomodoro.FocusLeft(10 * 60, 25));
        Assert.Equal(24 * 60, Pomodoro.FocusLeft(26 * 60, 25));
        Assert.Equal("12:05", Pomodoro.Countdown(12 * 60 + 5));
        Assert.Equal("0:01", Pomodoro.Countdown(0.4));
        Assert.Equal("0:00", Pomodoro.Countdown(-0.01));
    }

    [Fact]
    public void RemindersComeInWorkingTimeWhenNothingRuns()
    {
        var time = new UserTime(Zagreb);
        var schedule = new ReminderSchedule([1, 2, 3, 4, 5], 9 * 60, 17 * 60, 30);
        Assert.True(schedule.IsWorkingTime(new DateTimeOffset(2026, 9, 29, 7, 30, 0, TimeSpan.Zero), time));
        Assert.False(schedule.IsWorkingTime(new DateTimeOffset(2026, 9, 29, 6, 30, 0, TimeSpan.Zero), time));
        Assert.False(schedule.IsWorkingTime(new DateTimeOffset(2026, 9, 29, 15, 0, 0, TimeSpan.Zero), time));
        Assert.False(schedule.IsWorkingTime(new DateTimeOffset(2026, 10, 3, 8, 0, 0, TimeSpan.Zero), time));
        var now = new DateTimeOffset(2026, 9, 29, 8, 0, 0, TimeSpan.Zero);
        Assert.True(schedule.ShouldRemind(now, time, false, 0, null, null));
        Assert.False(schedule.ShouldRemind(now, time, true, 0, null, null));
        Assert.False(schedule.ShouldRemind(now, time, false, 0, now.AddSeconds(-600), null));
        Assert.True(schedule.ShouldRemind(now, time, false, 0, now.AddSeconds(-1800), null));
        Assert.False(schedule.ShouldRemind(now, time, false, 600, null, null));
    }

    [Fact]
    public void UndoPutsTheEntryBackButLeavesATimerStoppedSinceStopped()
    {
        var start = new DateTimeOffset(2026, 9, 29, 9, 0, 0, TimeSpan.Zero);
        var running = new Core.TimeEntryResponse { Id = Guid.NewGuid(), Description = "Agents", ProjectId = Guid.NewGuid(), Start = start };
        var added = Guid.NewGuid();
        var change = new SavedChange(running, [added]);

        // The timer was switched: the entry stopped at 10:42 and the next one runs.
        var stillRunning = change.Restoring([running with { Stop = start.AddMinutes(102) }, new Core.TimeEntryResponse { Id = added, Start = start.AddMinutes(102) }])!;
        Assert.Equal((start, (DateTimeOffset?)null), (stillRunning.Start, stillRunning.Stop));
        Assert.Equal(running.ProjectId, stillRunning.ProjectId);

        var stoppedSince = change.Restoring([running with { Stop = start.AddMinutes(102) }, new Core.TimeEntryResponse { Id = added, Start = start.AddMinutes(102), Stop = start.AddMinutes(160) }])!;
        Assert.Equal(start.AddMinutes(160), stoppedSince.Stop);
        Assert.Null(new SavedChange(null, [added]).Restoring([]));
    }
}
