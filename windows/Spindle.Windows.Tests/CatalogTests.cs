using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless;
using Spindle.Core;
using Spindle.Windows.Views;
using static Spindle.Windows.Tests.TestUi;

namespace Spindle.Windows.Tests;

public sealed class CatalogTests
{
    [Fact]
    public async Task NewProjectAndTagAreCreatedFromTheirSheets()
    {
        await using var session = HeadlessUnitTestSession.StartNew(typeof(HeadlessApp));
        await session.Dispatch(async () =>
        {
            using var fixture = new ApiFixture();
            TestData.SeedWeek(fixture, running: false);
            var window = await SignedIn(fixture);
            await Click(window, "Projects");
            await WaitFor(() => ErrorText(window).Contains("Website redesign"));
            await Preview(window, "projects");
            Tap(window, "New project");
            await WaitFor(() => window.Dialogs.IsVisible);
            Controls<TextBox>(window.Dialogs).Single(x => x.Name == "ProjectName").Text = "Mobile app";
            await Preview(window, "project-editor");
            await ClickNamed(window.Dialogs, "SaveProject");
            await WaitFor(() => !window.Dialogs.IsVisible);
            var created = JsonSerializer.Deserialize<ProjectRequest>(fixture.Writes.Single(w => w.Path.EndsWith("/projects")).Body, SpindleApiClient.JsonOptions)!;
            Assert.Equal("Mobile app", created.Name);
            Assert.StartsWith("#", created.Color);
            Assert.True(created.IsBillable);
            Assert.Contains(window.Projects, p => p.Name == "Mobile app");
            var tasks = ProjectTasks.Show(window, fixture.Projects[0].Id, () => { });
            await WaitFor(() => window.Dialogs.IsVisible);
            await Preview(window, "project-tasks");
            window.Dialogs.Close(null);
            await tasks;
            await Click(window, "Clients");
            await Preview(window, "clients");
            await Click(window, "Tags");
            Tap(window, "New tag");
            await WaitFor(() => window.Dialogs.IsVisible);
            Controls<TextBox>(window.Dialogs).Single(x => x.Name == "SheetName").Text = "overtime";
            await ClickNamed(window.Dialogs, "SheetSave");
            await WaitFor(() => window.Tags.Any(t => t.Name == "overtime"));
            await Preview(window, "tags");
            window.Quit();
            return 0;
        }, CancellationToken.None);
    }
}
