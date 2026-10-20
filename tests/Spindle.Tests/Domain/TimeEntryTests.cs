using Spindle.Domain.Common;
using Spindle.Domain.TimeEntries;

namespace Spindle.Tests.Domain;

public sealed class TimeEntryTests
{
    private static readonly DateTimeOffset Nine = new(2026, 9, 28, 9, 0, 0, TimeSpan.FromHours(2));

    [Fact]
    public void Times_are_stored_in_utc()
    {
        var entry = Create(start: Nine, stop: Nine.AddHours(1));

        Assert.Equal(TimeSpan.Zero, entry.Start.Offset);
        Assert.Equal(Nine, entry.Start);
        Assert.Equal(Nine.AddHours(1), entry.Stop);
    }

    [Fact]
    public void Without_an_end_the_timer_runs()
    {
        Assert.True(Create(start: Nine, stop: null).IsRunning);
    }

    [Fact]
    public void The_end_must_not_be_before_the_start()
    {
        var error = Assert.Throws<DomainValidationException>(() => Create(start: Nine, stop: Nine.AddSeconds(-1)));

        Assert.Equal("stop", error.Field);
    }

    [Fact]
    public void An_entry_can_be_at_most_999_hours_long()
    {
        Create(start: Nine, stop: Nine + TimeEntry.MaxDuration);

        Assert.Throws<DomainValidationException>(() => Create(start: Nine, stop: Nine + TimeEntry.MaxDuration + TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void A_task_needs_its_project()
    {
        var error = Assert.Throws<DomainValidationException>(() =>
            Create(Nine, Nine.AddHours(1), new TimeEntryDetails(null, ProjectId: null, TaskId: Guid.NewGuid(), [], false)));

        Assert.Equal("taskId", error.Field);
    }

    [Fact]
    public void Description_is_trimmed_and_limited()
    {
        var entry = Create(Nine, null, new TimeEntryDetails("  Writing specs  ", null, null, [], false));
        Assert.Equal("Writing specs", entry.Description);

        var tooLong = new string('a', TimeEntry.DescriptionMaxLength + 1);
        Assert.Throws<DomainValidationException>(() => Create(Nine, null, new TimeEntryDetails(tooLong, null, null, [], false)));
    }

    [Fact]
    public void Tags_are_kept_once_each()
    {
        var tag = Guid.NewGuid();

        var entry = Create(Nine, null, new TimeEntryDetails(null, null, null, [tag, tag], false));

        Assert.Equal([tag], entry.TagIds);
    }

    [Fact]
    public void Stopping_ends_the_timer()
    {
        var entry = Create(start: Nine, stop: null);

        entry.StopAt(Nine.AddMinutes(25));

        Assert.False(entry.IsRunning);
        Assert.Equal(Nine.AddMinutes(25), entry.Stop);
        Assert.Throws<DomainValidationException>(() => entry.StopAt(Nine.AddMinutes(30)));
    }

    [Fact]
    public void A_stopped_entry_cannot_run_again()
    {
        var entry = Create(start: Nine, stop: Nine.AddHours(1));

        var error = Assert.Throws<DomainValidationException>(() => entry.Update(Details(), Nine, stop: null));

        Assert.Equal("stop", error.Field);
    }

    [Fact]
    public void A_client_can_choose_the_id()
    {
        var id = Guid.NewGuid();

        Assert.Equal(id, TimeEntry.Create(id, Guid.NewGuid(), Guid.NewGuid(), Details(), Nine, null).Id);
    }

    private static TimeEntryDetails Details() => new("Work", null, null, [], false);

    private static TimeEntry Create(DateTimeOffset start, DateTimeOffset? stop, TimeEntryDetails? details = null) =>
        TimeEntry.Create(null, Guid.NewGuid(), Guid.NewGuid(), details ?? Details(), start, stop);
}
