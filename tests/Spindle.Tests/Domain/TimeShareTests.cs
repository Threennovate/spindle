using Spindle.Domain.Common;
using Spindle.Domain.TimeEntries;

namespace Spindle.Tests.Domain;

public sealed class TimeShareTests
{
    private static readonly DateTimeOffset Nine = new(2026, 9, 28, 9, 0, 0, TimeSpan.FromHours(2));
    private static readonly Guid Website = Guid.NewGuid();
    private static readonly Guid Internal = Guid.NewGuid();
    private static readonly Guid Research = Guid.NewGuid();

    [Theory]
    [InlineData(3600, new[] { 60, 40 }, new long[] { 2160, 1440 })]
    [InlineData(100, new[] { 34, 33, 33 }, new long[] { 34, 33, 33 })]
    [InlineData(10, new[] { 33, 33, 34 }, new long[] { 3, 3, 4 })]
    [InlineData(1, new[] { 50, 50 }, new long[] { 1, 0 })]
    [InlineData(0, new[] { 50, 50 }, new long[] { 0, 0 })]
    public void Seconds_are_divided_to_add_up_exactly(long seconds, int[] percents, long[] expected)
    {
        var parts = TimeShare.Allocate(seconds, percents);

        Assert.Equal(expected, parts);
        Assert.Equal(seconds, parts.Sum());
    }

    [Fact]
    public void A_split_entry_shows_its_largest_part_as_its_project()
    {
        var entry = Create(Split(new TimeShare(Internal, null, false, 30), new TimeShare(Website, null, true, 70)));

        Assert.True(entry.IsSplit);
        Assert.Equal(Website, entry.ProjectId);
        Assert.True(entry.IsBillable);
        Assert.Equal([30, 70], entry.Parts.Select(p => p.Percent));
    }

    [Fact]
    public void Equal_parts_keep_the_first_as_the_project()
    {
        var entry = Create(Split(new TimeShare(Internal, null, false, 50), new TimeShare(Website, null, true, 50)));

        Assert.Equal(Internal, entry.ProjectId);
    }

    [Fact]
    public void One_part_is_no_split()
    {
        var entry = Create(Split(new TimeShare(Website, null, true, 100)));

        Assert.False(entry.IsSplit);
        Assert.Empty(entry.Shares);
        Assert.Equal(Website, entry.ProjectId);
        Assert.Equal([100], entry.Parts.Select(p => p.Percent));
    }

    [Theory]
    [InlineData(60, 30)]
    [InlineData(0, 100)]
    [InlineData(100, 0)]
    public void Parts_must_add_up_to_100_percent_each_with_some(int first, int second)
    {
        var error = Assert.Throws<DomainValidationException>(() =>
            Create(Split(new TimeShare(Website, null, false, first), new TimeShare(Internal, null, false, second))));

        Assert.Equal("shares", error.Field);
    }

    [Fact]
    public void Each_part_needs_a_different_project_or_task()
    {
        var task = Guid.NewGuid();

        Assert.Throws<DomainValidationException>(() =>
            Create(Split(new TimeShare(Website, null, false, 50), new TimeShare(Website, null, false, 50))));
        Assert.True(Create(Split(new TimeShare(Website, null, false, 50), new TimeShare(Website, task, false, 50))).IsSplit);
    }

    [Fact]
    public void A_split_has_at_most_six_parts()
    {
        var seven = Enumerable.Range(0, 7).Select(i => new TimeShare(Guid.NewGuid(), null, false, i == 0 ? 16 : 14)).ToArray();

        Assert.Throws<DomainValidationException>(() => Create(Split(seven)));
    }

    [Fact]
    public void A_deleted_project_leaves_its_part_without_a_project()
    {
        var entry = Create(Split(
            new TimeShare(Website, null, true, 50),
            new TimeShare(Internal, null, false, 30),
            new TimeShare(Research, null, false, 20)));

        entry.RemoveProject(Internal);

        Assert.Equal([(Website, 50), (null, 30), (Research, 20)], entry.Parts.Select(p => (p.ProjectId, p.Percent)));
    }

    [Fact]
    public void Parts_that_become_the_same_are_joined_and_one_left_is_no_split()
    {
        var entry = Create(Split(new TimeShare(Website, null, true, 60), new TimeShare(Internal, null, false, 40)));

        entry.RemoveProject(Website);
        entry.RemoveProject(Internal);

        Assert.False(entry.IsSplit);
        Assert.Null(entry.ProjectId);
        Assert.True(entry.IsBillable);
    }

    [Fact]
    public void A_deleted_task_leaves_its_part_on_the_project()
    {
        var task = Guid.NewGuid();
        var entry = Create(Split(new TimeShare(Website, task, true, 60), new TimeShare(Website, null, false, 40)));

        entry.RemoveTask(task);

        Assert.False(entry.IsSplit);
        Assert.Equal(Website, entry.ProjectId);
        Assert.Null(entry.TaskId);
    }

    private static TimeEntryDetails Split(params TimeShare[] shares) => new("Two things", null, null, [], false, shares);

    private static TimeEntry Create(TimeEntryDetails details) =>
        TimeEntry.Create(null, Guid.NewGuid(), Guid.NewGuid(), details, Nine, Nine.AddHours(3));
}
