using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Spindle.Core;
using Spindle.Windows.Model;
using Spindle.Windows.Views;
using static Spindle.Windows.Tests.TestUi;

namespace Spindle.Windows.Tests;

public sealed class EntryEditorTests
{
    [Fact]
    public async Task EditingSplitPreservesEachProjectsTaskBillableSettingTagsAndTimes()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessApp));
        await session.Dispatch(async () =>
        {
            using var fixture = new ApiFixture();
            var projects = fixture.Projects;
            projects[0] = projects[0] with { ArchivedAt = TestClock.Now.AddDays(-1) };
            var tag = new TagResponse { Id = Guid.NewGuid(), Name = "Research" };
            fixture.Tags = [tag];
            var started = TestClock.Now.AddDays(-2).AddSeconds(-17);
            var entry = new TimeEntryResponse
            {
                Id = Guid.NewGuid(), WorkspaceId = fixture.Workspace, Description = "Parallel work", Start = started,
                Stop = started.AddHours(1).AddSeconds(29), ProjectId = projects[0].Id, TaskId = projects[0].Tasks[0].Id,
                IsBillable = true, TagIds = [tag.Id], Shares =
                [
                    new() { ProjectId = projects[0].Id, TaskId = projects[0].Tasks[0].Id, IsBillable = true, Percent = 65 },
                    new() { ProjectId = projects[1].Id, TaskId = projects[1].Tasks[0].Id, IsBillable = false, Percent = 35 }
                ]
            };
            fixture.Entries.Add(entry);
            var window = await SignedIn(fixture);
            var editing = window.EditAsync(window.Entries.Single());
            var editor = await Dialog<EntryEditor>(window);
            Controls<TextBox>(editor).Single(x => x.Name == "EditorDescription").Text = "Reviewed parallel work";
            await Preview(window, "editor-split");
            await ClickNamed(editor, "EditorSave");
            await editing;
            Assert.True(editor.Saved, ErrorText(editor));
            var write = Assert.Single(fixture.Writes);
            Assert.Equal(HttpMethod.Put, write.Method);
            Assert.EndsWith($"/time-entries/{entry.Id}", write.Path);
            var request = JsonSerializer.Deserialize<TimeEntryRequest>(write.Body, SpindleApiClient.JsonOptions)!;
            Assert.Equal("Reviewed parallel work", request.Description);
            Assert.Equal(entry.ProjectId, request.ProjectId);
            Assert.Equal(entry.TaskId, request.TaskId);
            Assert.True(request.IsBillable);
            Assert.Equal(entry.TagIds, request.TagIds);
            // A description-only change keeps the times to the second.
            Assert.Equal(entry.Start, request.Start);
            Assert.Equal(entry.Stop, request.Stop);
            Assert.Collection(request.Shares!,
                share => AssertShare(entry.Shares[0], share),
                share => AssertShare(entry.Shares[1], share));
            window.Quit();
            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task SplitNotTotalingHundredShowsErrorWithoutSendingMutation()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessApp));
        await session.Dispatch(async () =>
        {
            using var fixture = new ApiFixture();
            var window = await SignedIn(fixture);
            var projects = fixture.Projects;
            var yesterday = window.Time.At(window.Time.Today().AddDays(-1), 9, 0);
            var editing = window.EditAsync(null, yesterday, start: new EditorStart(Split: [new SplitPart(projects[0].Id, null, true, 70), new SplitPart(projects[1].Id, null, false, 20)]));
            var editor = await Dialog<EntryEditor>(window);
            await ClickNamed(editor, "EditorSave");
            Assert.False(editor.Saved);
            Assert.Empty(fixture.Writes);
            Assert.Contains("totaling 100%", ErrorText(editor));
            window.Dialogs.Close(null);
            await editing;
            window.Quit();
            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task NewEntryKeepsProjectTaskTagsAndTypedLocalTimes()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessApp));
        await session.Dispatch(async () =>
        {
            using var fixture = new ApiFixture();
            var tag = new TagResponse { Id = Guid.NewGuid(), Name = "Design" };
            fixture.Tags = [tag];
            var window = await SignedIn(fixture);
            var projects = fixture.Projects;
            var day = window.Time.Today().AddDays(-1);
            var draft = new EntryDraft { ProjectId = projects[0].Id, TaskId = projects[0].Tasks[0].Id, TagIds = [tag.Id], IsBillable = true };
            var editing = window.EditAsync(null, window.Time.At(day, 9, 0), start: new EditorStart(Details: draft, Description: "Design timer"));
            var editor = await Dialog<EntryEditor>(window);
            Controls<ClockField>(editor).Single(x => x.Name == "EditorStart").Text = "915";
            Controls<ClockField>(editor).Single(x => x.Name == "EditorEnd").Text = "10:45";
            await Preview(window, "editor-new");
            await ClickNamed(editor, "EditorSave");
            await editing;
            Assert.True(editor.Saved, ErrorText(editor));
            var write = Assert.Single(fixture.Writes);
            Assert.Equal(HttpMethod.Post, write.Method);
            var request = JsonSerializer.Deserialize<CreateTimeEntryRequest>(write.Body, SpindleApiClient.JsonOptions)!;
            Assert.NotEqual(Guid.Empty, request.Id);
            Assert.Equal("Design timer", request.Description);
            Assert.Equal(projects[0].Id, request.ProjectId);
            Assert.Equal(projects[0].Tasks[0].Id, request.TaskId);
            Assert.True(request.IsBillable);
            Assert.Equal(new[] { tag.Id }, request.TagIds);
            Assert.Equal(window.Time.At(day, 9, 15), request.Start);
            Assert.Equal(window.Time.At(day, 10, 45), request.Stop);
            window.Quit();
            return 0;
        }, CancellationToken.None);
    }

    [Theory]
    [InlineData("2026-03-29", "02:30", "does not exist")]
    [InlineData("2026-10-25", "02:30", "occurs twice")]
    public async Task DaylightSavingBoundaryRequiresUnambiguousLocalTime(string day, string localTime, string expected)
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessApp));
        await session.Dispatch(async () =>
        {
            using var fixture = new ApiFixture();
            var window = await SignedIn(fixture);
            var date = DateOnly.Parse(day);
            var editing = window.EditAsync(null, window.Time.At(date, 0, 30));
            var editor = await Dialog<EntryEditor>(window);
            Controls<ClockField>(editor).Single(x => x.Name == "EditorStart").Text = localTime;
            await ClickNamed(editor, "EditorSave");
            Assert.Contains(expected, ErrorText(editor));
            Assert.Empty(fixture.Writes);
            window.Dialogs.Close(null);
            await editing;
            window.Quit();
            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task StoppingAskForMissingRequiredDetailsBeforeStopping()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessApp));
        await session.Dispatch(async () =>
        {
            using var fixture = new ApiFixture();
            fixture.Settings = fixture.Settings with { RequireProject = true };
            fixture.Entries.Add(new TimeEntryResponse { Id = Guid.NewGuid(), WorkspaceId = fixture.Workspace, Description = "No project yet", Start = TestClock.Now.AddMinutes(-20) });
            var window = await SignedIn(fixture);
            var stopping = window.StopAsync();
            var editor = await Dialog<EntryEditor>(window);
            Assert.Contains("Stop timer", ErrorText(editor));
            await ClickNamed(editor, "EditorSave");
            Assert.Contains("A project is required in this workspace.", ErrorText(editor));
            Assert.Empty(fixture.Writes);
            await Preview(window, "editor-stopping");
            window.Dialogs.Close(null);
            await stopping;
            Assert.NotNull(window.Running);
            window.Quit();
            return 0;
        }, CancellationToken.None);
    }

    private static void AssertShare(TimeShareResponse expected, TimeShareRequest actual)
    {
        Assert.Equal(expected.ProjectId, actual.ProjectId);
        Assert.Equal(expected.TaskId, actual.TaskId);
        Assert.Equal(expected.IsBillable, actual.IsBillable);
        Assert.Equal(expected.Percent, actual.Percent);
    }
}
