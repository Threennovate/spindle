using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Spindle.Activity;
using Spindle.Core;
using Spindle.Windows.Model;
using Spindle.Windows.Views;
using static Spindle.Windows.Tests.TestUi;

namespace Spindle.Windows.Tests;

/// <summary>A split entry described per project, the Mac's cases for the shared rules and the editor.</summary>
public sealed class SplitDescriptionTests
{
    private static readonly Guid A = Guid.NewGuid(), B = Guid.NewGuid(), C = Guid.NewGuid();

    [Fact]
    public void DescribingEachPartGivesTheDescriptionToTheLargestAndStartsTheOthersEmpty()
    {
        SplitPart[] parts = [new(A, null, false, 30), new(B, null, false, 40), new(C, null, false, 30)];
        var described = SplitShares.DescribingEach(parts, " Fix login ");

        Assert.Equal(new[] { "", "Fix login", "" }, described.Select(p => p.Description));
        Assert.True(SplitShares.DescribesEach(described));
        Assert.False(SplitShares.DescribesEach(parts));
        Assert.False(SplitShares.DescribesEach([described[1]]));
        // On a tie the first of the largest gets it, as the server and the web choose the entry's project.
        Assert.Equal(new[] { "x", "" }, SplitShares.DescribingEach(parts[..2].Select(p => p with { Percent = 50 }).ToArray(), "x").Select(p => p.Description));
    }

    [Fact]
    public void OneDescriptionForAllJoinsThePartsOwn()
    {
        var parts = SplitShares.DescribingEach([new(A, null, false, 60), new(B, null, false, 40)], "Fix login");
        parts[1] = parts[1] with { Description = " Review PR " };
        var (shared, description) = SplitShares.SharingOneDescription(parts);

        Assert.Equal("Fix login; Review PR", description);
        Assert.All(shared, p => Assert.Null(p.Description));
        Assert.Equal("Fix login", SplitShares.SharingOneDescription([parts[0], new SplitPart(C, null, false, 0, "")]).Description);
    }

    [Fact]
    public void ARequiredDescriptionIsRequiredOfEachPartDescribedOnItsOwn()
    {
        var rules = new EntryRules(new WorkspaceSettingsResponse { RequireDescription = true, RequireProject = true, RequireTag = true }, false);
        SplitPart[] parts = [new(A, null, false, 60, "Design review"), new(B, null, false, 40, " ")];

        Assert.Empty(rules.Missing("", A, null, [Guid.NewGuid()], true, null, describesParts: true));
        Assert.Equal(new Dictionary<int, string> { [1] = EntryRules.DescriptionRequired }, rules.MissingInParts(parts, Catalog.Empty));
        // Parts sharing one description are not asked for their own; the entry's is.
        var (shared, _) = SplitShares.SharingOneDescription(parts);
        Assert.Empty(rules.MissingInParts(shared, Catalog.Empty));
        Assert.Equal(new[] { "description" }, rules.Missing("", A, null, [Guid.NewGuid()], true, null, SplitShares.DescribesEach(shared)).Select(m => m.Field));
    }

    [Fact]
    public async Task DescribingPerProjectSavesEachPartsOwnDescription()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessApp));
        await session.Dispatch(async () =>
        {
            using var fixture = new ApiFixture();
            var entry = Split(fixture, "Parallel work");
            var window = await SignedIn(fixture);
            var editing = window.EditAsync(window.Entries.Single());
            var editor = await Dialog<EntryEditor>(window);
            Assert.Contains("Shown on all 2 projects, timesheet links included", ErrorText(editor));
            await Preview(window, "editor-split-one-description");

            await ClickNamed(editor, "EditorDescribePerProject");
            Assert.False(Controls<TextBox>(editor).Single(x => x.Name == "EditorDescription").IsEffectivelyVisible);
            Assert.DoesNotContain("Shown on all", ErrorText(editor));
            Assert.True(ErrorText(editor).Contains("Split and described per project"), ErrorText(editor));
            var first = Part(editor, 0);
            var second = Part(editor, 1);
            Assert.Equal("Parallel work", first.Text);
            Assert.Equal("", second.Text);
            Assert.Equal($"What did you do on {fixture.Projects[1].Name}?", second.PlaceholderText);
            second.Text = "Review the API";
            await Preview(window, "editor-split-per-project");

            await ClickNamed(editor, "EditorSave");
            await editing;
            Assert.True(editor.Saved, ErrorText(editor));
            var request = JsonSerializer.Deserialize<TimeEntryRequest>(Assert.Single(fixture.Writes).Body, SpindleApiClient.JsonOptions)!;
            Assert.True(request.DescribeParts);
            Assert.Equal(new[] { "Parallel work", "Review the API" }, request.Shares!.Select(s => s.Description));
            Assert.Equal("Parallel work; Review the API", request.Description);
            // The list shows the parts' descriptions joined, as the server makes the entry's.
            var saved = window.Entries.Single(e => e.Id == entry.Id);
            Assert.Equal("Parallel work; Review the API", saved.Description);
            Assert.Equal(new[] { "Parallel work", "Review the API" }, saved.Shares.Select(s => s.Description));
            window.Quit();
            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task OneDescriptionForAllJoinsThemBackAndSaysSo()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessApp));
        await session.Dispatch(async () =>
        {
            using var fixture = new ApiFixture();
            Split(fixture, "Fix login; Review PR", "Fix login", "Review PR");
            var window = await SignedIn(fixture);
            var editing = window.EditAsync(window.Entries.Single());
            var editor = await Dialog<EntryEditor>(window);
            Assert.Equal("Review PR", Part(editor, 1).Text);

            await ClickNamed(editor, "SplitOneDescription");
            var description = Controls<TextBox>(editor).Single(x => x.Name == "EditorDescription");
            Assert.True(description.IsEffectivelyVisible);
            Assert.Equal("Fix login; Review PR", description.Text);
            Assert.DoesNotContain(Controls<TextBox>(editor), x => x.Name?.StartsWith("SplitDescription") == true);

            await ClickNamed(editor, "EditorSave");
            await editing;
            var request = JsonSerializer.Deserialize<TimeEntryRequest>(Assert.Single(fixture.Writes).Body, SpindleApiClient.JsonOptions)!;
            Assert.False(request.DescribeParts);
            Assert.All(request.Shares!, s => Assert.Null(s.Description));
            Assert.Equal("Fix login; Review PR", request.Description);
            window.Quit();
            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task EachPartMustBeDescribedWhereTheWorkspaceRequiresIt()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessApp));
        await session.Dispatch(async () =>
        {
            using var fixture = new ApiFixture();
            fixture.Settings = fixture.Settings with { RequireDescription = true };
            Split(fixture, "Fix login", "Fix login", "");
            var window = await SignedIn(fixture);
            var editing = window.EditAsync(window.Entries.Single());
            var editor = await Dialog<EntryEditor>(window);
            Assert.Contains("Split and described per project (required)", ErrorText(editor));

            await ClickNamed(editor, "EditorSave");
            Assert.False(editor.Saved);
            Assert.Empty(fixture.Writes);
            Assert.Contains(EntryRules.DescriptionRequired, ErrorText(editor));
            await Preview(window, "editor-split-per-project-required");
            window.Dialogs.Close(null);
            await editing;
            window.Quit();
            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ChangingOnlyTimesKeepsThePartsOwnDescriptions()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessApp));
        await session.Dispatch(async () =>
        {
            using var fixture = new ApiFixture();
            var entry = Split(fixture, "Fix login; Review PR", "Fix login", "Review PR");
            var window = await SignedIn(fixture);
            // As the calendar moves an entry.
            var current = window.Entries.Single();
            await window.MutateAsync(() => window.UpdateEntryAsync(current, current.Request() with { Start = current.Start.AddMinutes(30), Stop = current.Stop!.Value.AddMinutes(30) }));
            var request = JsonSerializer.Deserialize<TimeEntryRequest>(Assert.Single(fixture.Writes).Body, SpindleApiClient.JsonOptions)!;
            Assert.Equal("Fix login; Review PR", request.Description);
            var moved = window.Entries.Single(e => e.Id == entry.Id);
            Assert.Equal(new[] { "Fix login", "Review PR" }, moved.Shares.Select(s => s.Description));
            Assert.Equal("Fix login; Review PR", moved.Description);
            window.Quit();
            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ContinueKeepsEachPartsOwnDescription()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessApp));
        await session.Dispatch(async () =>
        {
            using var fixture = new ApiFixture();
            var entry = Split(fixture, "Fix login; Review PR", "Fix login", "Review PR");
            var window = await SignedIn(fixture);
            await ClickNamed(window, "Continue");
            await WaitFor(() => fixture.Entries.Count == 2);

            AssertCopiedOnItsOwn(fixture, entry);
            Assert.Null(fixture.Entries.Single(e => e.Id != entry.Id).Stop);
            window.Quit();
            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task DuplicateKeepsEachPartsOwnDescription()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessApp));
        await session.Dispatch(async () =>
        {
            using var fixture = new ApiFixture();
            var entry = Split(fixture, "Fix login; Review PR", "Fix login", "Review PR");
            var window = await SignedIn(fixture);
            var menu = (MenuFlyout)Controls<Button>(window).Single(x => x.Name == "EntryMore").Flyout!;
            menu.Items.OfType<MenuItem>().Single(x => x.Header as string == "Duplicate").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await WaitFor(() => fixture.Entries.Count == 2);

            AssertCopiedOnItsOwn(fixture, entry);
            var copy = fixture.Entries.Single(e => e.Id != entry.Id);
            Assert.Equal((entry.Start, entry.Stop), (copy.Start, copy.Stop));
            window.Quit();
            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task PickingAPreviousEntryKeepsEachPartsOwnDescription()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessApp));
        await session.Dispatch(async () =>
        {
            using var fixture = new ApiFixture();
            var entry = Split(fixture, "Fix login; Review PR", "Fix login", "Review PR");
            var window = await SignedIn(fixture);
            var field = Controls<DescriptionField>(window).Single();
            field.Box.Focus();
            field.Text = "Review";
            await WaitFor(() => Controls<Button>(field).Any(x => x.Classes.Contains("row")));
            KeyDown(field.Box, Key.Down);
            KeyDown(field.Box, Key.Enter);
            await WaitFor(() => fixture.Entries.Count == 2);

            AssertCopiedOnItsOwn(fixture, entry);
            window.Quit();
            return 0;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task EndingASplitOnAnEmptyPartDoesNotTakeTheOthersDescription()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessApp));
        await session.Dispatch(async () =>
        {
            using var fixture = new ApiFixture();
            // Only the second project's part was described, so the entry's description is its text alone.
            var entry = Split(fixture, "Review PR", "", "Review PR");
            var window = await SignedIn(fixture);
            var at = entry.Start.AddHours(1).ToUnixTimeMilliseconds();
            await window.Splits.EndAndContinueAsync(window.Entries.Single(), new SplitDrift([fixture.Projects[1].Id], null, at));

            var next = fixture.Entries.Single(e => e.Id != entry.Id);
            Assert.Equal(fixture.Projects[0].Id, next.ProjectId);
            Assert.Empty(next.Shares);
            Assert.Equal("", next.Description);
            Assert.Equal(at, fixture.Entries.Single(e => e.Id == entry.Id).Stop!.Value.ToUnixTimeMilliseconds());
            window.Quit();
            return 0;
        }, CancellationToken.None);
    }

    /// <summary>The new entry was sent and saved with each part's own description, so neither project shows the other's.</summary>
    private static void AssertCopiedOnItsOwn(ApiFixture fixture, TimeEntryResponse entry)
    {
        var request = JsonSerializer.Deserialize<CreateTimeEntryRequest>(fixture.Writes.Last(w => w.Method == HttpMethod.Post && w.Path.EndsWith("/time-entries")).Body, SpindleApiClient.JsonOptions)!;
        Assert.True(request.DescribeParts);
        Assert.Equal(new[] { "Fix login", "Review PR" }, request.Shares!.Select(s => s.Description));
        var copy = fixture.Entries.Single(e => e.Id != entry.Id);
        Assert.Equal(new[] { "Fix login", "Review PR" }, copy.Shares.Select(s => s.Description));
        Assert.Equal(new Guid?[] { fixture.Projects[0].Id, fixture.Projects[1].Id }, copy.Shares.Select(s => s.ProjectId));
        Assert.Equal("Fix login; Review PR", copy.Description);
    }

    private static void KeyDown(Control control, Key key) => control.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key });

    private static TextBox Part(Control editor, int index) => Controls<TextBox>(editor).Single(x => x.Name == $"SplitDescription{index}");

    private static TimeEntryResponse Split(ApiFixture fixture, string description, params string?[] parts)
    {
        var projects = fixture.Projects;
        var started = TestClock.Now.AddDays(-2);
        var entry = new TimeEntryResponse
        {
            Id = Guid.NewGuid(), WorkspaceId = fixture.Workspace, Description = description, Start = started, Stop = started.AddHours(2),
            ProjectId = projects[0].Id, IsBillable = true, Shares =
            [
                new() { ProjectId = projects[0].Id, IsBillable = true, Percent = 60, Description = parts.ElementAtOrDefault(0) },
                new() { ProjectId = projects[1].Id, IsBillable = false, Percent = 40, Description = parts.ElementAtOrDefault(1) },
            ],
        };
        fixture.Entries.Add(entry);
        return entry;
    }
}
