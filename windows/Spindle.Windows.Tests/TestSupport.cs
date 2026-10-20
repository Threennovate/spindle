using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Styling;
using Spindle.Core;
using Spindle.Platform;
using Spindle.Windows.Model;

[assembly: Xunit.v3.Parallelization(Mode = Xunit.Sdk.ParallelMode.None)]

namespace Spindle.Windows.Tests;

public sealed class HeadlessApp : App
{
    static HeadlessApp()
    {
        // Tests never read the developer's own agent logs.
        var home = Path.Combine(Path.GetTempPath(), "spindle-agent-home-" + Environment.ProcessId);
        Directory.CreateDirectory(home);
        Environment.SetEnvironmentVariable("SPINDLE_AGENT_HOME", home);
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<HeadlessApp>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
        .WithInterFont();
}

/// <summary>What the UI tests share: finding controls, clicking them, and drawing the window for review.</summary>
internal static class TestUi
{
    /// <summary>
    /// With SPINDLE_UI_PREVIEW set to a folder, draws the window in the light and the dark theme, as
    /// <c>name.png</c> and <c>name-dark.png</c>, so changes can be looked at without a Windows PC.
    /// </summary>
    internal static async Task Preview(Window window, string name, bool dark = true)
    {
        var directory = Environment.GetEnvironmentVariable("SPINDLE_UI_PREVIEW");
        if (string.IsNullOrWhiteSpace(directory)) return;
        if (window is MainWindow)
        {
            window.Width = 1360;
            window.Height = 860;
        }
        Directory.CreateDirectory(directory);
        foreach (var (variant, suffix) in dark ? new[] { (ThemeVariant.Light, ""), (ThemeVariant.Dark, "-dark") } : [(ThemeVariant.Light, "")])
        {
            Application.Current!.RequestedThemeVariant = variant;
            await Task.Delay(120);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            await Task.Delay(30);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            using var bitmap = window.CaptureRenderedFrame();
            Assert.NotNull(bitmap);
            bitmap.Save(Path.Combine(directory, name + suffix + ".png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        }
        Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
    }

    internal static IEnumerable<T> Controls<T>(Control control) where T : Control => control.GetLogicalDescendants().OfType<T>();

    internal static string ErrorText(Control control) => string.Join(" ", Controls<TextBlock>(control).Select(x => x.Text));

    internal static string Label(Button button) => button.Content as string ?? AutomationProperties.GetName(button) ?? "";

    /// <summary>Clicks the button with this text or accessible name, and waits until its action finished.</summary>
    internal static async Task Click(Control root, string text)
    {
        var button = Controls<Button>(root).Single(x => x.IsVisible && Label(x) == text);
        await Press(button);
        Assert.True(button.IsEnabled, $"'{text}' did not finish.");
    }

    /// <summary>Clicks a button whose action stays open, e.g. one that shows a dialog.</summary>
    internal static void Tap(Control root, string text) =>
        Controls<Button>(root).Single(x => x.IsVisible && Label(x) == text).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    internal static async Task ClickNamed(Control root, string name)
    {
        var button = Controls<Button>(root).Single(x => x.Name == name);
        await Press(button);
    }

    internal static async Task Press(Button button)
    {
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        for (var i = 0; !button.IsEnabled && i < 200; i++) await Task.Delay(10);
    }

    internal static async Task WaitFor(Func<bool> ready, int tries = 200)
    {
        for (var i = 0; !ready() && i < tries; i++) await Task.Delay(10);
        Assert.True(ready());
    }

    /// <summary>A window signed in to the fixture's account, showing the timer.</summary>
    internal static async Task<MainWindow> SignedIn(ApiFixture fixture)
    {
        await fixture.SignIn();
        // At the PC: a build machine's real idle time would hold reminders back.
        var window = new MainWindow(fixture.Api, new PreferencesStore(Path.Combine(fixture.DataDirectory, "preferences.json")), fixture.DataDirectory, () => TimeSpan.Zero);
        window.Show();
        await WaitFor(() => Controls<TextBox>(window).Any(x => x.Name is "TimerDescription" or "RunningDescription"));
        await Task.Delay(50);
        return window;
    }

    /// <summary>Waits for an in-window dialog and returns what it shows.</summary>
    internal static async Task<T> Dialog<T>(MainWindow window) where T : Control
    {
        await WaitFor(() => window.Dialogs.IsVisible && window.Dialogs.Content is T);
        return (T)window.Dialogs.Content!;
    }
}

internal sealed record Write(HttpMethod Method, string Path, string Body);

/// <summary>A server in memory, answering as the Spindle API does, and recording what was sent.</summary>
internal sealed class ApiFixture : HttpMessageHandler
{
    public static readonly TimeZoneInfo Zagreb = TimeZoneInfo.FindSystemTimeZoneById("Europe/Zagreb");

    public Guid Workspace { get; set; } = Guid.NewGuid();
    public Guid Organization { get; } = Guid.NewGuid();
    public Guid UserId { get; set; } = Guid.NewGuid();
    public string DataDirectory { get; } = Path.Combine(Path.GetTempPath(), "spindle-ui-data-" + Guid.NewGuid());
    public List<Write> Writes { get; } = [];
    public List<Write> AuthRequests { get; } = [];
    public ClientResponse[] Clients { get; set; }
    public ProjectResponse[] Projects { get; set; }
    public RepositoryLinkResponse[] RepositoryLinks { get; set; } = [];
    public TagResponse[] Tags { get; set; } = [];
    public WorkspaceSettingsResponse Settings { get; set; } = new() { Name = "Test workspace", Currency = "EUR" };
    public OrganizationAccess Access { get; set; } = OrganizationAccess.Full;
    public PreferencesResponse Preferences { get; set; } = new() { WeekStart = 1, DateFormat = "d MMM yyyy", TimeFormat = "24h", DurationFormat = "hms", PomodoroMinutes = 25, PomodoroBreakMinutes = 5 };
    public List<TimeEntryResponse> Entries { get; } = [];
    public SummaryReport Summary { get; set; } = new() { Currency = "EUR", Totals = new() { Seconds = 3600, BillableSeconds = 1800, EntryCount = 1 } };
    public DetailedReport Detailed { get; set; } = new() { Currency = "EUR", Page = 1, PageSize = 50 };
    public WeeklyReport Weekly { get; set; } = new() { Currency = "EUR" };
    /// <summary>The account's agent activity sync setting, as /api/me/agent-activity/settings and /api/me tell it.</summary>
    public AgentActivitySettingsResponse AgentActivity { get; set; } = new();
    /// <summary>Holds a change of the agent activity setting until this completes, or fails it.</summary>
    public TaskCompletionSource? HoldSettingsChange { get; set; }
    public bool FailSettingsChange { get; set; }
    public SpindleApiClient Api { get; }
    /// <summary>What /api/server says, e.g. the sign-in providers it offers.</summary>
    public ServerInfo Server { get; set; } = new(false, true, "terms", "privacy");
    /// <summary>False for an account made with Google or Apple; null as from a server that doesn't say.</summary>
    public bool? HasPassword { get; set; }

    public ApiFixture()
    {
        Clients = [new() { Id = Guid.NewGuid(), Name = "Acme", ActiveProjectCount = 1 }];
        Projects = TestData.Projects(Clients[0].Id);
        Api = new(new HttpClient(this) { BaseAddress = new Uri("https://test.spindle.invalid") }, new MemorySession());
    }

    public Task SignIn() => Api.LoginAsync(new() { Email = "test@example.com", Password = "password" });

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri!.AbsolutePath == "/api/server") return Json(Server);
        if (request.RequestUri.AbsolutePath == "/api/auth/forgot-password")
        {
            AuthRequests.Add(new(request.Method, request.RequestUri.AbsolutePath, await request.Content!.ReadAsStringAsync(cancellationToken)));
            return new(HttpStatusCode.NoContent);
        }
        if (request.RequestUri.AbsolutePath == "/api/auth/external/exchange")
        {
            AuthRequests.Add(new(request.Method, request.RequestUri.AbsolutePath, await request.Content!.ReadAsStringAsync(cancellationToken)));
            return Json(new ExternalSignInResponse { AccessToken = "test-token", RefreshToken = "refresh-token", AccessTokenExpiresAt = DateTimeOffset.UtcNow.AddHours(1), NewAccount = true });
        }
        if (request.RequestUri.AbsolutePath is "/api/auth/login" or "/api/auth/register")
        {
            AuthRequests.Add(new(request.Method, request.RequestUri.AbsolutePath, await request.Content!.ReadAsStringAsync(cancellationToken)));
            return Json(new AuthTokensResponse { AccessToken = "test-token", RefreshToken = "refresh-token", AccessTokenExpiresAt = DateTimeOffset.UtcNow.AddHours(1) });
        }
        if (request.RequestUri.AbsolutePath == "/api/auth/logout") return new(HttpStatusCode.NoContent);
        Assert.Equal("test-token", request.Headers.Authorization?.Parameter);
        var path = request.RequestUri.AbsolutePath;
        if (path.Contains("/agent-proposals", StringComparison.Ordinal)) return await ProposalsAsync(request, path, cancellationToken);
        if (request.Method == HttpMethod.Get)
        {
            if (path == "/api/me") return Json(new MeResponse
            {
                Id = UserId, FullName = "Ana Kovač", Email = "ana@example.com", EmailConfirmed = true, TimeZone = "Europe/Zagreb", DefaultWorkspaceId = Workspace,
                Preferences = Preferences, AgentActivity = AgentActivity, HasPassword = HasPassword,
                Organizations = [new() { Id = Organization, Name = "Kovač Studio", Role = OrganizationRole.Owner, Access = Access }],
                Workspaces = [new() { Id = Workspace, OrganizationId = Organization, Name = "Kovač Studio", Role = WorkspaceRole.Admin }],
            });
            if (path == "/api/me/sessions") return Json(Array.Empty<object>());
            if (path == "/api/me/agent-activity/settings") return Json(AgentActivity);
            if (path == "/api/me/agent-activity/links") return Json(RepositoryLinks);
            if (path == "/api/me/agent-activity/dismissals") return Json(Array.Empty<Guid>());
            if (path.EndsWith("/projects")) return Json(Projects);
            if (path.EndsWith("/settings")) return Json(Settings);
            if (path.EndsWith("/clients")) return Json(Clients);
            if (path.EndsWith("/members")) return Json(Array.Empty<MemberResponse>());
            if (path.EndsWith("/groups")) return Json(Array.Empty<GroupResponse>());
            if (path.EndsWith("/tags")) return Json(Tags);
            if (path.EndsWith("/time-entries/suggestions")) return Json(Entries.Where(e => e.Stop is not null).DistinctBy(e => e.Description)
                .Select(e => new TimeEntrySuggestion { Description = e.Description, ProjectId = e.ProjectId, TaskId = e.TaskId, TagIds = e.TagIds, IsBillable = e.IsBillable, Shares = e.Shares }).ToArray());
            if (path.EndsWith("/time-entries/current")) return Entries.FirstOrDefault(x => x.Stop == null) is { } running ? Json(running) : new(HttpStatusCode.NoContent);
            if (path.EndsWith("/time-entries")) return Json(Entries.ToArray());
            if (path.Contains("/time-entries/", StringComparison.Ordinal) && Guid.TryParse(path.Split('/')[^1], out var id))
                return Entries.FirstOrDefault(x => x.Id == id) is { } entry ? Json(entry) : new(HttpStatusCode.NotFound);
        }
        if (path.EndsWith("/reports/summary"))
        {
            Writes.Add(new(request.Method, path, await request.Content!.ReadAsStringAsync(cancellationToken)));
            return Json(Summary);
        }
        if (path.EndsWith("/reports/detailed"))
        {
            Writes.Add(new(request.Method, path, await request.Content!.ReadAsStringAsync(cancellationToken)));
            return Json(Detailed);
        }
        if (path.EndsWith("/reports/weekly"))
        {
            Writes.Add(new(request.Method, path, await request.Content!.ReadAsStringAsync(cancellationToken)));
            return Json(Weekly);
        }
        if (request.Method == HttpMethod.Delete)
        {
            var id = Guid.Parse(path.Split('/')[^1]);
            Entries.RemoveAll(x => x.Id == id);
            Writes.Add(new(request.Method, path, ""));
            return new(HttpStatusCode.NoContent);
        }
        if (request.Method == HttpMethod.Post || request.Method == HttpMethod.Put)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Writes.Add(new(request.Method, request.RequestUri.AbsolutePath, body));
            if (path == "/api/me/agent-activity/settings")
            {
                if (HoldSettingsChange is { } hold) await hold.Task;
                if (FailSettingsChange) return new(HttpStatusCode.InternalServerError);
                var change = JsonSerializer.Deserialize<AgentActivitySettingsRequest>(body, SpindleApiClient.JsonOptions)!;
                AgentActivity = new() { Sync = change.Sync, Titles = change.Sync && change.Titles, SyncedSince = change.Sync ? AgentActivity.SyncedSince ?? DateTimeOffset.UtcNow : null };
                return Json(AgentActivity);
            }
            if (path.EndsWith("/stop"))
            {
                var id = Guid.Parse(path.Split('/')[^2]);
                var current = Entries.Single(x => x.Id == id);
                var stopped = current with { Stop = JsonSerializer.Deserialize<StopTimeEntryRequest>(body, SpindleApiClient.JsonOptions)!.Stop };
                Entries.Remove(current);
                Entries.Add(stopped);
                return Json(stopped);
            }
            if (path.EndsWith("/time-entries") && request.Method == HttpMethod.Post)
            {
                var create = JsonSerializer.Deserialize<CreateTimeEntryRequest>(body, SpindleApiClient.JsonOptions)!;
                var created = new TimeEntryResponse
                {
                    Id = create.Id ?? Guid.NewGuid(), WorkspaceId = Workspace, Description = create.Description ?? "", ProjectId = create.ProjectId,
                    TaskId = create.TaskId, IsBillable = create.IsBillable ?? false, TagIds = create.TagIds ?? [], Start = create.Start ?? DateTimeOffset.UtcNow, Stop = create.Stop,
                    Shares = create.Shares?.Select(s => new TimeShareResponse { ProjectId = s.ProjectId, TaskId = s.TaskId, Percent = s.Percent, IsBillable = s.IsBillable ?? false, Description = s.Description }).ToArray() ?? [],
                };
                var (createdShares, createdDescription) = SplitMath.Described(created.Shares, created.Description, create.DescribeParts, null);
                created = created with { Shares = createdShares, Description = createdDescription };
                Entries.Add(created);
                return Json(created);
            }
            if (request.Method == HttpMethod.Put && path.Contains("/time-entries/", StringComparison.Ordinal) && Guid.TryParse(path.Split('/')[^1], out var entryId))
            {
                var update = JsonSerializer.Deserialize<TimeEntryRequest>(body, SpindleApiClient.JsonOptions)!;
                var current = Entries.Single(x => x.Id == entryId);
                var changed = current with
                {
                    Description = update.Description ?? current.Description, ProjectId = update.ProjectId, TaskId = update.TaskId, TagIds = update.TagIds ?? current.TagIds,
                    IsBillable = update.IsBillable ?? current.IsBillable, Start = update.Start ?? current.Start, Stop = update.Stop,
                    Shares = update.Shares?.Select(s => new TimeShareResponse { ProjectId = s.ProjectId, TaskId = s.TaskId, Percent = s.Percent, IsBillable = s.IsBillable ?? false, Description = s.Description }).ToArray() ?? [],
                };
                var (changedShares, changedDescription) = SplitMath.Described(changed.Shares, update.Description ?? "", update.DescribeParts, current);
                changed = changed with { Shares = changedShares, Description = changedDescription };
                Entries[Entries.IndexOf(current)] = changed;
                return Json(changed);
            }
            if (path.EndsWith("/projects") && request.Method == HttpMethod.Post)
            {
                var create = JsonSerializer.Deserialize<ProjectRequest>(body, SpindleApiClient.JsonOptions)!;
                var project = new ProjectResponse { Id = Guid.NewGuid(), Name = create.Name, Color = create.Color, ClientId = create.ClientId, IsBillable = create.IsBillable, IsPrivate = create.IsPrivate, EstimateSeconds = create.EstimateSeconds, CanManage = true };
                Projects = [.. Projects, project];
                return Json(project);
            }
            if (path.EndsWith("/tags") && request.Method == HttpMethod.Post)
            {
                var tag = new TagResponse { Id = Guid.NewGuid(), Name = JsonSerializer.Deserialize<TagRequest>(body, SpindleApiClient.JsonOptions)!.Name };
                Tags = [.. Tags, tag];
                return Json(tag);
            }
            return Json(new TimeEntryResponse { Id = Guid.NewGuid(), WorkspaceId = Workspace });
        }
        throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}");
    }

    /// <summary>Proposals waiting on the server, in the agents' shapes.</summary>
    public List<AgentProposal> ServerProposals { get; } = [];
    /// <summary>What the server says stands in a proposal's way, by proposal: what blocks it and what is only a warning.</summary>
    private readonly Dictionary<string, (string[] Blocking, string[] Warnings)> proposalIssues = [];
    /// <summary>What the person answered on the server: the proposal, saved or discarded (or applied), and the entry.</summary>
    public List<(Guid Id, string Outcome, Guid? EntryId)> ProposalAnswers { get; } = [];
    private readonly List<AgentProposal> answered = [];

    /// <summary>Proposals an agent made through the server's MCP.</summary>
    public void Propose(IEnumerable<AgentProposal> proposals) => ServerProposals.AddRange(proposals);

    /// <summary>A proposal with what the server says stands in its way, in the server's words.</summary>
    public void Propose(AgentProposal proposal, string[]? blocking = null, string[]? warnings = null)
    {
        ServerProposals.Add(proposal);
        proposalIssues[proposal.Id] = (blocking ?? [], warnings ?? []);
    }

    private async Task<HttpResponseMessage> ProposalsAsync(HttpRequestMessage request, string path, CancellationToken cancellationToken)
    {
        if (request.Method == HttpMethod.Get)
        {
            return Json(new AgentProposalsResponse
            {
                Pending = ServerProposals.Where(p => p.WorkspaceId == Guid.Parse(path.Split('/')[3])).Select(p =>
                {
                    var (blocking, warnings) = proposalIssues.GetValueOrDefault(p.Id, ([], []));
                    return new AgentProposalResponse
                    {
                        Id = Guid.Parse(p.Id), WorkspaceId = p.WorkspaceId, EntryId = p.EntryId, Reason = p.Reason, Client = p.Client, ProposedAt = p.ProposedAt,
                        ExpiresAt = p.ProposedAt.AddDays(7),
                        Original = p.Original is { } o ? new() { Start = o.Start, Stop = o.Stop, Description = o.Description, ProjectId = o.ProjectId, TaskId = o.TaskId, IsBillable = o.IsBillable, Shares = o.Shares } : null,
                        Fields = new()
                        {
                            Start = p.Fields.Start, Stop = p.Fields.Stop, Description = p.Fields.Description,
                            Work = p.Fields.Work is { } w ? new() { ProjectId = w.ProjectId, TaskId = w.TaskId, IsBillable = w.IsBillable, Shares = w.Shares.Select(s => new TimeShareResponse { ProjectId = s.ProjectId, TaskId = s.TaskId, IsBillable = s.IsBillable, Percent = s.Percent, Description = s.Description }).ToArray() } : null,
                        },
                        Blocking = blocking,
                        Warnings = warnings,
                    };
                }).ToArray(),
            });
        }
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        Writes.Add(new(request.Method, path, body));
        var segments = path.Split('/');
        var id = segments[^2];
        if (segments[^1] == "reopen")
        {
            var back = answered.Single(p => p.Id == id);
            answered.Remove(back);
            ServerProposals.Add(back);
            ProposalAnswers.RemoveAll(a => a.Id == Guid.Parse(id));
            return new(HttpStatusCode.NoContent);
        }
        var proposal = ServerProposals.SingleOrDefault(p => p.Id == id);
        if (proposal is null) return new(HttpStatusCode.Conflict);
        ServerProposals.Remove(proposal);
        answered.Add(proposal);
        if (segments[^1] == "resolve")
        {
            using var json = JsonDocument.Parse(body);
            var entryId = json.RootElement.TryGetProperty("entryId", out var e) && e.ValueKind == JsonValueKind.String ? e.GetGuid() : (Guid?)null;
            ProposalAnswers.Add((proposal.Id is { } pid ? Guid.Parse(pid) : Guid.Empty, json.RootElement.GetProperty("outcome").GetString()!, entryId));
            return new(HttpStatusCode.NoContent);
        }
        // Applied: saved as proposed, on the entry as it is now.
        var current = proposal.EntryId is { } changed ? Entries.Single(x => x.Id == changed) : null;
        var saved = proposal.Request(current);
        var entry = new TimeEntryResponse
        {
            Id = current?.Id ?? Guid.NewGuid(), WorkspaceId = Workspace, Description = saved.Description ?? "", ProjectId = saved.ProjectId, TaskId = saved.TaskId,
            TagIds = saved.TagIds ?? [], IsBillable = saved.IsBillable ?? false, Start = saved.Start ?? DateTimeOffset.UtcNow, Stop = saved.Stop,
            Shares = saved.Shares?.Select(s => new TimeShareResponse { ProjectId = s.ProjectId, TaskId = s.TaskId, Percent = s.Percent, IsBillable = s.IsBillable ?? false, Description = s.Description }).ToArray() ?? [],
        };
        var (shares, description) = SplitMath.Described(entry.Shares, entry.Description, saved.DescribeParts, current);
        entry = entry with { Shares = shares, Description = description };
        if (current is null) Entries.Add(entry);
        else Entries[Entries.IndexOf(current)] = entry;
        ProposalAnswers.Add((Guid.Parse(proposal.Id), proposal.IsNew ? "added" : "changed", entry.Id));
        return Json(new AppliedAgentProposalResponse { ProposalId = Guid.Parse(proposal.Id), Entry = entry });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) DeleteDirectory(DataDirectory);
        base.Dispose(disposing);
    }

    /// <summary>
    /// Deletes a test's folder, waiting a moment for a save still closing its file: Windows doesn't delete files that
    /// are open.
    /// </summary>
    internal static void DeleteDirectory(string path)
    {
        for (var attempt = 1; Directory.Exists(path); attempt++)
        {
            try { Directory.Delete(path, true); }
            catch (IOException) when (attempt < 20) { Thread.Sleep(50); }
            catch (UnauthorizedAccessException) when (attempt < 20) { Thread.Sleep(50); }
        }
    }

    internal static HttpResponseMessage Json<T>(T body) => new(HttpStatusCode.OK) { Content = JsonContent.Create(body, options: SpindleApiClient.JsonOptions) };

    private sealed class MemorySession : ISessionStore
    {
        public Task<StoredSession?> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult<StoredSession?>(null);
        public Task SaveAsync(StoredSession session, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ClearAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}

/// <summary>The Mac's synthetic Claude Code and Codex logs, in a home folder of their own, with their repositories.</summary>
internal sealed class AgentLogs : IDisposable
{
    private readonly string? previous = Environment.GetEnvironmentVariable("SPINDLE_AGENT_HOME");

    public AgentLogs()
    {
        var fixtures = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../apple/Mac/Snapshots/Fixtures/agent-logs"));
        Home = Path.Combine(Path.GetTempPath(), "spindle-agent-fixture-" + Guid.NewGuid().ToString("N"));
        Copy(Path.Combine(fixtures, "claude"), Path.Combine(Home, ".claude"));
        Copy(Path.Combine(fixtures, "codex"), Path.Combine(Home, ".codex"));
        // The logs name these folders; each is a repository, as on the Mac that wrote them.
        foreach (var name in new[] { "atlas", "beacon", "copper" }) Directory.CreateDirectory(Path.Combine(Repository(name), ".git"));
        Environment.SetEnvironmentVariable("SPINDLE_AGENT_HOME", Home);
    }

    public string Home { get; }

    /// <summary>
    /// A repository the logs name, as this PC finds it: on Windows, <c>/tmp/spindle-activity-repos</c> is on the
    /// current drive, so its identity has the drive in it.
    /// </summary>
    public static string Repository(string name) => Path.GetFullPath(Path.Combine("/tmp/spindle-activity-repos", name));

    /// <summary>The day the synthetic logs are on.</summary>
    public static DateOnly Day => new(2026, 9, 28);

    private static void Copy(string from, string to)
    {
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("SPINDLE_AGENT_HOME", previous);
        ApiFixture.DeleteDirectory(Home);
    }
}

/// <summary>A week of realistic work, for snapshots that show the app as people use it.</summary>
internal static class TestData
{
    internal static ProjectResponse[] Projects(Guid? client = null) =>
    [
        new() { Id = Guid.NewGuid(), Name = "AnchorNet", Color = "#0B83D9", ClientId = client, IsBillable = true, TrackedSeconds = 41 * 3600, EstimateSeconds = 60 * 3600, CanManage = true,
            Tasks = [new() { Id = Guid.NewGuid(), Name = "Booking", TrackedSeconds = 12 * 3600 }, new() { Id = Guid.NewGuid(), Name = "Design review" }] },
        new() { Id = Guid.NewGuid(), Name = "Spindle", Color = "#E36A00", TrackedSeconds = 77 * 3600, CanManage = true, IsPrivate = true, Tasks = [new() { Id = Guid.NewGuid(), Name = "Windows", TrackedSeconds = 20 * 3600 }] },
        new() { Id = Guid.NewGuid(), Name = "Website redesign", Color = "#9E5BD9", ClientId = client, IsBillable = true, TrackedSeconds = 9 * 3600, EstimateSeconds = 8 * 3600, CanManage = true, Tasks = [] },
    ];

    /// <summary>Reports as the server sends them for a busy week.</summary>
    internal static SummaryReport Summary(ApiFixture fixture)
    {
        var p = fixture.Projects;
        var monday = DateOnly.FromDateTime(DateTime.Today).AddDays(-(((int)DateTime.Today.DayOfWeek + 6) % 7));
        var totals = new ReportTotals { Seconds = 41 * 3600 + 1800, BillableSeconds = 25 * 3600 + 1800, EntryCount = 38, Amount = 2295m };
        fixture.Detailed = new DetailedReport
        {
            Currency = "EUR", Totals = totals, Page = 1, PageSize = 50, TotalCount = 3,
            Entries =
            [
                new() { Id = Guid.NewGuid(), UserName = "Ana Kovač", Description = "Booking flow with agents", ProjectId = p[0].Id, ProjectName = p[0].Name, ProjectColor = p[0].Color, TaskName = "Booking", IsBillable = true, Tags = ["review"], Start = DateTimeOffset.Now.AddHours(-30), Stop = DateTimeOffset.Now.AddHours(-28), Seconds = 7200, Amount = 180m, Percent = 60 },
                new() { Id = Guid.NewGuid(), UserName = "Ana Kovač", Description = "Windows timer bar", ProjectId = p[1].Id, ProjectName = p[1].Name, ProjectColor = p[1].Color, ClientName = null, Start = DateTimeOffset.Now.AddHours(-26), Stop = DateTimeOffset.Now.AddHours(-23), Seconds = 10800, Percent = 100 },
                new() { Id = Guid.NewGuid(), UserName = "Ivan Horvat", Description = "", ProjectName = null, Start = DateTimeOffset.Now.AddHours(-50), Stop = DateTimeOffset.Now.AddHours(-49), Seconds = 3600, Percent = 100 },
            ],
        };
        fixture.Weekly = new WeeklyReport
        {
            Currency = "EUR", Totals = totals, Days = Enumerable.Range(0, 7).Select(monday.AddDays).ToArray(),
            DayTotals = [8 * 3600, 9 * 3600, 7 * 3600 + 1800, 8 * 3600, 9 * 3600, 0, 0],
            Rows =
            [
                new() { Key = p[0].Id.ToString(), Name = p[0].Name, Color = p[0].Color, Detail = "Acme", Days = [4 * 3600, 5 * 3600, 3 * 3600, 4 * 3600, 5 * 3600, 0, 0], Seconds = 21 * 3600, Amount = 1890m },
                new() { Key = p[1].Id.ToString(), Name = p[1].Name, Color = p[1].Color, Days = [4 * 3600, 4 * 3600, 4 * 3600 + 1800, 4 * 3600, 4 * 3600, 0, 0], Seconds = 20 * 3600 + 1800, Amount = 405m },
            ],
        };
        return new SummaryReport
        {
            Currency = "EUR", Totals = totals, Resolution = ReportResolution.Day,
            Buckets = Enumerable.Range(0, 7).Select(i => new ReportBucket { Start = monday.AddDays(i), Seconds = i < 5 ? (7 + i % 3) * 3600 : 0, BillableSeconds = i < 5 ? (4 + i % 2) * 3600 : 0 }).ToArray(),
            Groups =
            [
                new() { Key = p[0].Id.ToString(), Name = p[0].Name, Color = p[0].Color, Detail = "Acme", Seconds = 21 * 3600, BillableSeconds = 21 * 3600, Amount = 1890m,
                    SubGroups = [new() { Key = "a", Name = "Booking flow with agents", Seconds = 14 * 3600, Amount = 1260m }, new() { Key = "b", Name = "Design review", Seconds = 7 * 3600, Amount = 630m }] },
                new() { Key = p[1].Id.ToString(), Name = p[1].Name, Color = p[1].Color, Seconds = 16 * 3600, Amount = 0m },
                new() { Key = p[2].Id.ToString(), Name = p[2].Name, Color = p[2].Color, Detail = "Acme", Seconds = 3 * 3600, BillableSeconds = 3 * 3600, Amount = 405m },
                new() { Key = "none", Name = "No project", Seconds = 5400 },
            ],
        };
    }

    /// <summary>Entries over the last days, with a split one and, when asked, a running one.</summary>
    internal static void SeedWeek(ApiFixture fixture, bool running)
    {
        var projects = fixture.Projects;
        var review = new TagResponse { Id = Guid.NewGuid(), Name = "review" };
        var meeting = new TagResponse { Id = Guid.NewGuid(), Name = "meeting" };
        fixture.Tags = [review, meeting];
        var zone = ApiFixture.Zagreb;
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone).Date);
        DateTimeOffset At(int daysAgo, double hour)
        {
            var local = today.AddDays(-daysAgo).ToDateTime(TimeOnly.MinValue).AddHours(hour);
            return new DateTimeOffset(local, zone.GetUtcOffset(local));
        }
        void Add(string description, int daysAgo, double from, double to, ProjectResponse? project, Guid? task = null, Guid[]? tags = null, TimeShareResponse[]? shares = null)
        {
            var start = At(daysAgo, from);
            var stop = At(daysAgo, to);
            if (stop > DateTimeOffset.UtcNow) return;
            fixture.Entries.Add(new TimeEntryResponse
            {
                Id = Guid.NewGuid(), WorkspaceId = fixture.Workspace, Description = description, ProjectId = project?.Id, TaskId = task, IsBillable = project?.IsBillable ?? false,
                TagIds = tags ?? [], Start = start, Stop = stop, Shares = shares ?? [],
            });
        }
        Add("Standup", 1, 9, 9.25, projects[1], tags: [meeting.Id]);
        Add("Booking flow with agents", 1, 9.5, 12.25, projects[0], shares:
        [
            new() { ProjectId = projects[0].Id, TaskId = projects[0].Tasks[0].Id, IsBillable = true, Percent = 60 },
            new() { ProjectId = projects[1].Id, Percent = 40 },
        ]);
        Add("Design review with Acme", 1, 13, 14.5, projects[2], tags: [review.Id, meeting.Id]);
        Add("Windows timer bar", 1, 14.5, 17.75, projects[1], projects[1].Tasks[0].Id);
        Add("Release notes", 2, 10, 11.5, projects[1]);
        Add("Hero illustrations", 2, 12, 15, projects[2], tags: [review.Id]);
        Add("Invoices", 3, 16, 16.75, null);
        Add("Standup", 0, 9, 9.25, projects[1], tags: [meeting.Id]);
        Add("Calendar drag to create", 0, 9.5, 11, projects[1], projects[1].Tasks[0].Id);
        if (running)
        {
            fixture.Entries.Add(new TimeEntryResponse
            {
                Id = Guid.NewGuid(), WorkspaceId = fixture.Workspace, Description = "Review PR for the booking flow", ProjectId = projects[0].Id,
                TaskId = projects[0].Tasks[0].Id, IsBillable = true, TagIds = [review.Id], Start = DateTimeOffset.UtcNow.AddMinutes(-47).AddSeconds(-12),
            });
        }
    }
}
