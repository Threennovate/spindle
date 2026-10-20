using System.Net;
using Spindle.Core;

// Run only against an isolated disposable development API/database. Never point this at a personal or hosted account.
var server = args.Length > 0 ? args[0] : "http://127.0.0.1:5287";
if (!Uri.TryCreate(server, UriKind.Absolute, out var uri) || !uri.IsLoopback)
    throw new ArgumentException("Live checks require a loopback disposable API.");
using var network = new SwitchableHandler();
using var http = new HttpClient(network) { BaseAddress = uri };
var session = new MemorySession();
var api = new SpindleApiClient(http, session);
var email = $"windows-live-{Guid.NewGuid():N}@example.com";
const string password = "LiveCheck-Disposable-123!";
await api.RegisterAsync(new() { Email = email, Password = password, FullName = "Windows live checks", TimeZone = "Europe/Zagreb", AcceptTerms = true });
var me = await api.GetMeAsync();
Check(api.IsAuthenticated && me.Id != Guid.Empty, "register/me");
var workspaces = await api.GetWorkspacesAsync();
var workspace = workspaces.First();
var ws = workspace.Id;
await api.GetWorkspaceSettingsAsync(ws);
await api.LogoutAsync();
await api.LoginAsync(new() { Email = email, Password = password });
Check(api.IsAuthenticated, "login/logout");
var restored = new SpindleApiClient(http, session);
Check(await restored.RestoreSessionAsync(), "refresh token rotation/session restore");
api = restored;
var client = await api.CreateClientAsync(ws, new() { Name = "Live client" });
client = await api.UpdateClientAsync(ws, client.Id, new() { Name = "Live client updated" });
var first = await api.CreateProjectAsync(ws, new() { Name = "First live project", Color = "#2563eb", ClientId = client.Id, IsBillable = true });
var second = await api.CreateProjectAsync(ws, new() { Name = "Second live project", Color = "#16a34a" });
var task = await api.CreateTaskAsync(ws, first.Id, new() { Name = "Live task" });
task = await api.UpdateTaskAsync(ws, first.Id, task.Id, new() { Name = "Live task updated", IsDone = false });
var tag = await api.CreateTagAsync(ws, new() { Name = "live" });
tag = await api.UpdateTagAsync(ws, tag.Id, new() { Name = "live-updated" });
Check((await api.GetProjectsAsync(ws)).Any(p => p.Id == first.Id) && (await api.GetTagsAsync(ws)).Any(t => t.Id == tag.Id), "catalog clients/projects/tasks/tags create/update/read");
var start = DateTimeOffset.UtcNow.AddHours(-3);
var timer = await api.CreateTimeEntryAsync(ws, new() { Id = Guid.NewGuid(), Description = "Timer", ProjectId = first.Id, TaskId = task.Id, TagIds = [tag.Id], Start = start });
Check((await api.GetCurrentTimeEntryAsync())?.Id == timer.Id, "start/current timer");
timer = await api.StopTimeEntryAsync(ws, timer.Id, start.AddMinutes(20));
Check(timer.Stop.HasValue && await api.GetCurrentTimeEntryAsync() is null, "stop/current empty");
var split = await api.CreateTimeEntryAsync(ws, new() { Id = Guid.NewGuid(), Description = "Split", Start = start.AddMinutes(30), Stop = start.AddMinutes(90), Shares = [new() { ProjectId = first.Id, IsBillable = true, Percent = 60 }, new() { ProjectId = second.Id, IsBillable = false, Percent = 40 }] });
Check(split.Shares.Length == 2 && split.ProjectId == first.Id, "manual split creation/largest share");
split = await api.UpdateTimeEntryAsync(ws, split, new() { Description = "Split updated", Start = split.Start, Stop = split.Stop, TagIds = [tag.Id], Shares = split.Shares.Select(s => new TimeShareRequest { ProjectId = s.ProjectId, TaskId = s.TaskId, IsBillable = s.IsBillable, Percent = s.Percent }).ToArray() });
Check((await api.GetTimeEntriesAsync(ws, start.AddDays(-1), start.AddDays(1))).Length == 2, "entries read/update");
Check((await api.GetTimeEntryAsync(ws, split.Id))?.Description == "Split updated", "exact entry lookup");
var day = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow.AddDays(-2), TimeZoneInfo.FindSystemTimeZoneById("Europe/Zagreb")).ToString("yyyy-MM-dd");
await api.PostAsync($"/api/workspaces/{ws:D}/agent-proposals/entries", new
{
    client = "Codex",
    entries = new[] { new { start = $"{day}T09:00", stop = $"{day}T10:00", description = "Proposed one", projectId = first.Id.ToString() }, new { start = $"{day}T09:30", stop = $"{day}T11:00", description = "Proposed two", projectId = second.Id.ToString() } },
});
var proposals = await api.GetAgentProposalsAsync(ws);
Check(proposals.Pending.Length == 2 && proposals.Pending.All(p => p.Client == "Codex" && p.Warnings.Length > 0 && p.Blocking.Length == 0), "agent proposals listed with the server's warnings");
var applied = await api.ApplyAgentProposalAsync(ws, proposals.Pending[0].Id);
await api.DiscardAgentProposalAsync(ws, proposals.Pending[1].Id);
Check(applied.Entry.Description == "Proposed one" && (await api.GetAgentProposalsAsync(ws)).Pending.Length == 0, "agent proposal applied and discarded");
await api.ReopenAgentProposalAsync(ws, proposals.Pending[1].Id);
await api.SavedAgentProposalAsync(ws, proposals.Pending[1].Id, applied.Entry.Id);
Check((await api.GetAgentProposalsAsync(ws)).Pending.Length == 0, "agent proposal reopened and saved");
await api.DeleteTimeEntryAsync(ws, applied.Entry);
using (var strangerHttp = new HttpClient { BaseAddress = uri })
{
    var stranger = new SpindleApiClient(strangerHttp, new MemorySession());
    await stranger.RegisterAsync(new() { Email = $"stranger-{Guid.NewGuid():N}@example.com", Password = password, FullName = "Stranger check", TimeZone = "Europe/Zagreb", AcceptTerms = true });
    try { Check(await stranger.GetTimeEntryAsync(ws, split.Id) is null, "stranger workspace entry is unavailable"); }
    catch (ApiException ex) when (ex.StatusCode == HttpStatusCode.Forbidden) { Check(true, "stranger workspace access denied"); }
    await stranger.LogoutAsync();
}
var date = DateOnly.FromDateTime(start.UtcDateTime);
var summaryRequest = new SummaryReportRequest { From = date.AddDays(-1), To = date.AddDays(1), GroupBy = ReportGrouping.Project, SubGroupBy = null };
var summary = await api.GetSummaryReportAsync(ws, summaryRequest);
Check(summary.Totals.Seconds == 4800 && summary.Groups.Single(g => g.Id == first.Id).Seconds == 3360 && summary.Groups.Single(g => g.Id == second.Id).Seconds == 1440, "summary split allocation totals");
var detailRequest = new DetailedReportRequest { From = date.AddDays(-1), To = date.AddDays(1) };
Check((await api.GetDetailedReportAsync(ws, detailRequest)).Totals.Seconds == 4800, "detailed report");
var weekRequest = new WeeklyReportRequest { From = date.AddDays(-1), To = date.AddDays(1), GroupBy = ReportGrouping.Project };
Check((await api.GetWeeklyReportAsync(ws, weekRequest)).Totals.Seconds == 4800, "weekly report");
foreach (var format in new[] { "csv", "xlsx", "pdf" })
{
    var exported = await api.ExportSummaryReportAsync(ws, summaryRequest, format);
    Check(exported.Content.Length > 0 && exported.FileName.EndsWith("." + format), $"summary {format} export");
}
Check((await api.ExportDetailedReportAsync(ws, detailRequest, "csv")).Content.Length > 0, "detailed export");
Check((await api.ExportWeeklyReportAsync(ws, weekRequest, "csv")).Content.Length > 0, "weekly export");
await api.GetTimeEntrySuggestionsAsync(ws, "Split");
await api.GetSplitSuggestionsAsync(ws, start.AddDays(-1), start.AddDays(1));
var cacheDir = Path.Combine(Path.GetTempPath(), "spindle-windows-live", Guid.NewGuid().ToString("N"));
try
{
    var offline = new OfflineTracker(api, cacheDir, me.Id);
    await offline.LoadAsync();
    await offline.SynchronizeAsync(ws, start.AddDays(-1), start.AddDays(1));
    network.Offline = true;
    var pending = await offline.CreateAsync(ws, new() { Id = Guid.NewGuid(), Description = "Offline durable entry", Start = start.AddHours(2), Stop = start.AddHours(2).AddMinutes(10), ProjectId = second.Id });
    Check(offline.IsOffline && offline.PendingChanges.Count == 1, "offline durable create");
    offline = new OfflineTracker(api, cacheDir, me.Id);
    await offline.LoadAsync();
    Check(offline.PendingChanges.Count == 1, "offline outbox survives reload");
    network.Offline = false;
    await offline.SynchronizeAsync(ws, start.AddDays(-1), start.AddDays(1));
    Check(!offline.IsOffline && offline.PendingChanges.Count == 0 && offline.GetEntries(ws).Any(e => e.Id == pending.Id), "offline reconnect replay");
    await offline.DeleteAsync(pending);
    Check(offline.PendingChanges.Count == 0, "offline tracker online deletion");
    network.Offline = true;
    await offline.UpdateAsync(split, new() { Description = "Local pending edit", Start = split.Start, Stop = split.Stop, ProjectId = split.ProjectId, Shares = split.Shares.Select(s => new TimeShareRequest { ProjectId = s.ProjectId, IsBillable = s.IsBillable, Percent = s.Percent }).ToArray() });
    network.Offline = false;
    var moved = await api.UpdateTimeEntryAsync(ws, split, new() { Description = "Another device moved entry", Start = split.Start.AddDays(-30), Stop = split.Stop!.Value.AddDays(-30), ProjectId = split.ProjectId, Shares = split.Shares.Select(s => new TimeShareRequest { ProjectId = s.ProjectId, IsBillable = s.IsBillable, Percent = s.Percent }).ToArray() });
    Check((await api.GetTimeEntryAsync(ws, split.Id))?.Start == moved.Start, "exact lookup after another device moves date");
    await offline.SynchronizeAsync(ws, start.AddDays(-1), start.AddDays(1));
    Check(offline.PendingChanges.Single().Error is not null, "offline conflict preserves another device edit");
    await offline.DiscardPendingAsync(offline.PendingChanges.Single().Id);
    Check(offline.PendingChanges.Count == 0 && offline.GetEntries(ws).Any(e => e.Id == moved.Id && e.Start == moved.Start), "discard conflict restores moved server entry");
    split = moved;
}
finally { network.Offline = false; if (Directory.Exists(cacheDir)) Directory.Delete(cacheDir, true); }
await api.DeleteTimeEntryAsync(ws, timer);
await api.DeleteTimeEntryAsync(ws, split);
Check(await api.GetTimeEntryAsync(ws, split.Id) is null, "exact deleted entry lookup returns null");
await api.DeleteTaskAsync(ws, first.Id, task.Id);
await api.DeleteTagAsync(ws, tag.Id);
await api.ArchiveProjectAsync(ws, first.Id);
await api.ArchiveProjectAsync(ws, first.Id, restore: true);
await api.DeleteProjectAsync(ws, first.Id);
await api.DeleteProjectAsync(ws, second.Id);
await api.ArchiveClientAsync(ws, client.Id);
await api.ArchiveClientAsync(ws, client.Id, restore: true);
await api.DeleteClientAsync(ws, client.Id);
Check((await api.GetProjectsAsync(ws)).Length == 0 && (await api.GetTagsAsync(ws)).Length == 0, "catalog archive/restore/delete");
await api.LogoutAsync();
Console.WriteLine("PASS: live Windows Core checks completed against disposable API.");

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException("FAIL: " + message);
    Console.WriteLine("PASS: " + message);
}
sealed class MemorySession : ISessionStore
{
    private StoredSession? _session;
    public Task<StoredSession?> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(_session);
    public Task SaveAsync(StoredSession session, CancellationToken cancellationToken = default) { _session = session; return Task.CompletedTask; }
    public Task ClearAsync(CancellationToken cancellationToken = default) { _session = null; return Task.CompletedTask; }
}
sealed class SwitchableHandler : DelegatingHandler
{
    public bool Offline { get; set; }
    public SwitchableHandler() : base(new HttpClientHandler()) { }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Offline ? throw new HttpRequestException("Simulated disconnected network") : base.SendAsync(request, cancellationToken);
}
