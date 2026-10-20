using System.Net;
using System.Text.Json;
using Spindle.Core;

var tests = new (string Name, Func<Task> Run)[]
{
    ("Expired parallel requests rotate one refresh token", RefreshSingleFlight),
    ("401 retry rotates once and repeats authorized call", UnauthorizedRetry),
    ("Restore persists rotated token; revoked session clears", Restore),
    ("Lost create response is never automatically retried", NoAmbiguousRetry),
    ("ProblemDetails preserves validation and read-only state", Problems),
    ("Tracking contracts preserve IDs, instants and split shares", Contracts),
    ("Report exports preserve file bytes and safe name", Export),
    ("Logout removes credentials when server is unavailable", Logout),
    ("Split allocation preserves every second and stable ties", Splits),
    ("Part descriptions are kept, joined and dropped as the server does", PartDescriptions),
    ("Offline time changes keep part descriptions through replay", OfflinePartDescriptions),
    ("Offline creates survive restart and reconcile with a stable ID", OfflineCreate),
    ("Offline replay detects edits from another device", OfflineConflict),
    ("Offline edits replay on the version they were made on", OfflineVersions),
    ("Offline cold-start queues safely before authentication", OfflineColdStart),
    ("Invalid offline entry rules never become durable changes", OfflineValidation),
    ("Repeated create with changed server data stays a visible conflict", OfflineCreateConflict),
    ("PKCE challenge matches RFC 7636", PkceVector),
    ("External sign-in starts with the loopback redirect, PKCE and time zone", ExternalStart),
    ("Callbacks give the code or the error, and ignore other paths", ExternalCallbacks),
    ("Loopback listener answers the browser and hands back its code", LoopbackRoundTrip),
    ("Google sign-in exchanges the code with its verifier and keeps the session", ExternalSignInFlow),
};
foreach (var test in tests)
{
    await test.Run();
    Console.WriteLine($"PASS {test.Name}");
}
Console.WriteLine($"{tests.Length} API core checks passed.");

static AuthTokensResponse Tokens(string access = "access", string refresh = "refresh", bool expired = false) => new()
{
    AccessToken = access, RefreshToken = refresh,
    AccessTokenExpiresAt = DateTimeOffset.UtcNow.AddMinutes(expired ? -1 : 15),
    RefreshTokenExpiresAt = DateTimeOffset.UtcNow.AddDays(90),
};
static HttpResponseMessage Json(object value, HttpStatusCode code = HttpStatusCode.OK) => new(code)
{ Content = new StringContent(JsonSerializer.Serialize(value, SpindleApiClient.JsonOptions), System.Text.Encoding.UTF8, "application/json") };
static SpindleApiClient Client(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler, MemoryStore store) =>
    new(new HttpClient(new FakeHandler(handler)) { BaseAddress = new("https://test.spindle.invalid") }, store);
static Task Login(SpindleApiClient client) => client.LoginAsync(new LoginRequest { Email = "test@example.com", Password = "password" });
static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

static async Task RefreshSingleFlight()
{
    var refreshes = 0;
    var store = new MemoryStore();
    var client = Client(async request =>
    {
        if (request.RequestUri!.AbsolutePath == "/api/auth/login") return Json(Tokens(expired: true));
        if (request.RequestUri.AbsolutePath == "/api/auth/refresh")
        {
            Interlocked.Increment(ref refreshes);
            await Task.Delay(25);
            Assert((await request.Content!.ReadAsStringAsync()).Contains("refresh"), "Missing refresh token.");
            return Json(Tokens("new-access", "new-refresh"));
        }
        Assert(request.Headers.Authorization?.Parameter == "new-access", "Used expired access token.");
        return Json(new MeResponse());
    }, store);
    await Login(client);
    await Task.WhenAll(Enumerable.Range(0, 25).Select(_ => client.GetWorkspacesAsync()));
    Assert(refreshes == 1 && store.Session?.RefreshToken == "new-refresh", "Refresh was not single-flight or was not persisted.");
}

static async Task UnauthorizedRetry()
{
    var refreshes = 0;
    var store = new MemoryStore();
    var client = Client(request =>
    {
        if (request.RequestUri!.AbsolutePath == "/api/auth/login") return Task.FromResult(Json(Tokens()));
        if (request.RequestUri.AbsolutePath == "/api/auth/refresh")
        { Interlocked.Increment(ref refreshes); return Task.FromResult(Json(Tokens("new", "rotated"))); }
        return Task.FromResult(request.Headers.Authorization?.Parameter == "access"
            ? new HttpResponseMessage(HttpStatusCode.Unauthorized) : Json(new MeResponse()));
    }, store);
    await Login(client);
    await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => client.GetWorkspacesAsync()));
    Assert(refreshes == 1, "401 caused multiple token rotations.");
}

static async Task Restore()
{
    var store = new MemoryStore { Session = new("saved") };
    var client = Client(_ => Task.FromResult(Json(Tokens("restored", "rotated"))), store);
    Assert(await client.RestoreSessionAsync(), "Restore failed.");
    Assert(store.Session?.RefreshToken == "rotated" && client.IsAuthenticated, "Restored session was not saved.");
    client = Client(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)), store);
    Assert(!await client.RestoreSessionAsync() && store.Session is null, "Revoked token stayed in credential store.");
    store.Session = new("keep-on-transient-failure");
    client = Client(_ => throw new HttpRequestException("Offline"), store);
    try { await client.RestoreSessionAsync(); throw new InvalidOperationException("Offline restore succeeded."); }
    catch (HttpRequestException) { Assert(store.Session is not null, "Transient failure erased session."); }
}

static async Task NoAmbiguousRetry()
{
    var creates = 0;
    var client = Client(request =>
    {
        if (request.RequestUri!.AbsolutePath == "/api/auth/login") return Task.FromResult(Json(Tokens()));
        Interlocked.Increment(ref creates);
        throw new HttpRequestException("Response lost after server saved.");
    }, new());
    await Login(client);
    try { await client.CreateTimeEntryAsync(Guid.NewGuid(), new CreateTimeEntryRequest { Id = Guid.NewGuid(), Start = DateTimeOffset.UtcNow }); }
    catch (HttpRequestException) { }
    Assert(creates == 1, "Ambiguous create was retried.");
}

static async Task Problems()
{
    var client = Client(request => Task.FromResult(request.RequestUri!.AbsolutePath == "/api/auth/login" ? Json(Tokens()) :
        Json(new { type = "urn:spindle:problem:read-only", title = "Read only", errors = new { description = new[] { "Description required" } } }, HttpStatusCode.PaymentRequired)), new());
    await Login(client);
    try { await client.GetMeAsync(); throw new InvalidOperationException("Expected failure."); }
    catch (ApiException ex)
    { Assert(ex.IsReadOnly && ex.Errors["description"][0] == "Description required" && ex.Message.Contains("Description required"), "ProblemDetails lost useful state."); }
}

static async Task Contracts()
{
    var workspace = Guid.NewGuid();
    var id = Guid.NewGuid();
    var now = new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.FromHours(2));
    var shares = new[] { new TimeShareRequest { ProjectId = Guid.NewGuid(), Percent = 60 }, new TimeShareRequest { ProjectId = Guid.NewGuid(), Percent = 40 } };
    var client = Client(async request =>
    {
        var path = request.RequestUri!.AbsolutePath;
        if (path == "/api/auth/login") return Json(Tokens());
        if (path == "/api/me/time-entries/current") return new(HttpStatusCode.NoContent);
        Assert(path == $"/api/workspaces/{workspace:D}/time-entries", "Incorrect tracking URL.");
        if (request.Method == HttpMethod.Get)
        { Assert(request.RequestUri.Query.Contains("from=") && request.RequestUri.Query.Contains("to="), "Missing date range."); return Json(Array.Empty<TimeEntryResponse>()); }
        var body = JsonSerializer.Deserialize<CreateTimeEntryRequest>(await request.Content!.ReadAsStringAsync(), SpindleApiClient.JsonOptions)!;
        Assert(body.Id == id && body.Start == now && body.Shares!.Sum(s => s.Percent) == 100, "Create contract changed entry details.");
        return Json(new TimeEntryResponse { Id = id, WorkspaceId = workspace, Start = now }, HttpStatusCode.Created);
    }, new());
    await Login(client);
    Assert(await client.GetCurrentTimeEntryAsync() is null, "204 current entry not handled.");
    await client.GetTimeEntriesAsync(workspace, now.AddDays(-1), now);
    var saved = await client.CreateTimeEntryAsync(workspace, new CreateTimeEntryRequest { Id = id, Start = now, Shares = shares });
    Assert(saved.Id == id && saved.Start == now, "Create response incorrectly decoded.");
    var report = JsonSerializer.Deserialize<SummaryReport>("{\"currency\":\"EUR\",\"resolution\":\"day\",\"totals\":{\"seconds\":3600,\"amount\":12.5},\"groups\":[]}", SpindleApiClient.JsonOptions)!;
    Assert(report.Totals.Amount == 12.5m && report.Resolution == ReportResolution.Day, "Report enum or money decoded incorrectly.");
}

static async Task Export()
{
    var client = Client(request =>
    {
        if (request.RequestUri!.AbsolutePath == "/api/auth/login") return Task.FromResult(Json(Tokens()));
        Assert(request.RequestUri.AbsolutePath.EndsWith("/reports/summary/export") && request.RequestUri.Query == "?format=csv", "Incorrect export URL.");
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
        response.Content.Headers.ContentDisposition = new("attachment") { FileName = "\"../report.csv\"" };
        return Task.FromResult(response);
    }, new());
    await Login(client);
    var file = await client.ExportSummaryReportAsync(Guid.NewGuid(), new SummaryReportRequest { From = new(2026, 10, 1), To = new(2026, 10, 10) }, "csv");
    Assert(file.FileName == "report.csv" && file.Content.SequenceEqual(new byte[] { 1, 2, 3 }), "Export corrupted bytes or unsafe filename.");
}

static async Task Logout()
{
    var store = new MemoryStore();
    var client = Client(request => request.RequestUri!.AbsolutePath == "/api/auth/login"
        ? Task.FromResult(Json(Tokens())) : throw new HttpRequestException("Offline"), store);
    await Login(client);
    try { await client.LogoutAsync(); } catch (HttpRequestException) { }
    Assert(!client.IsAuthenticated && store.Session is null, "Offline logout left credentials.");
}

static Task Splits()
{
    Assert(SplitMath.Allocate(1, new[] { 50, 50 }).SequenceEqual(new long[] { 1, 0 }), "Tie did not favor first part.");
    Assert(SplitMath.Allocate(3599, new[] { 60, 40 }).SequenceEqual(new long[] { 2159, 1440 }), "Largest remainder allocation is wrong.");
    foreach (var seconds in new long[] { 0, 1, 59, 3600, long.MaxValue })
        Assert(SplitMath.Allocate(seconds, new[] { 33, 33, 34 }).Sum() == seconds, "Split added or lost time.");
    var first = Guid.NewGuid(); var second = Guid.NewGuid();
    var divided = SplitMath.Divide(new TimeEntryResponse { Shares = [new() { ProjectId = first, Percent = 60 }, new() { ProjectId = second, Percent = 40 }] }, 3600);
    Assert(divided[0].ProjectId == first && divided[0].Seconds == 2160 && divided[1].ProjectId == second && divided[1].Seconds == 1440, "Project time allocation changed.");
    return Task.CompletedTask;
}

static Task PartDescriptions()
{
    var first = Guid.NewGuid(); var second = Guid.NewGuid(); var third = Guid.NewGuid();
    TimeShareResponse[] Shares(params string?[] descriptions) =>
        [new() { ProjectId = first, Percent = 60, Description = descriptions.ElementAtOrDefault(0) }, new() { ProjectId = second, Percent = 40, Description = descriptions.ElementAtOrDefault(1) }];
    var described = new TimeEntryResponse { Description = "Fix login; Review PR", Shares = Shares("Fix login", "Review PR") };

    var saved = SplitMath.Described(Shares(" Fix login ", null), "ignored", true, null);
    Assert(saved.Description == "Fix login" && saved.Shares.Select(s => s.Description).SequenceEqual(["Fix login", ""]), "Described parts were not trimmed, kept and joined.");
    var shared = SplitMath.Described(Shares("Fix login", "Review PR"), " Both ", false, described);
    Assert(shared.Description == "Both" && shared.Shares.All(s => s.Description is null), "One description for all kept the parts' own.");
    // Apps that predate part descriptions send the entry's description as they were shown it.
    var kept = SplitMath.Described([.. Shares(null, null).Reverse(), new() { ProjectId = third, Percent = 0 }], "Fix login; Review PR", null, described);
    Assert(kept.Shares.Select(s => s.Description).SequenceEqual(["Review PR", "Fix login", ""]) && kept.Description == "Review PR; Fix login", "Unchanged description lost the parts' own.");
    var changed = SplitMath.Described(Shares(null, null), "Something else", null, described);
    Assert(changed.Description == "Something else" && changed.Shares.All(s => s.Description is null), "A changed description did not become one for all.");
    Assert(SplitMath.Described([new() { ProjectId = first, Percent = 100, Description = "Own" }], "One", true, described) is { Description: "One" } single && single.Shares[0].Description is null,
        "An entry that is not split kept a part description.");
    Assert(SplitMath.DescribesParts(described) && !SplitMath.DescribesParts(described with { Shares = Shares() }), "Described parts not recognised.");
    Assert(SplitMath.JoinDescriptions([" a ", null, "", "b"]) == "a; b", "Descriptions joined differently from the server.");
    return Task.CompletedTask;
}

static async Task OfflinePartDescriptions()
{
    var directory = Path.Combine(Path.GetTempPath(), "spindle-parts-test-" + Guid.NewGuid());
    try
    {
        var offline = false; var workspace = Guid.NewGuid(); var puts = new List<TimeEntryRequest>();
        // Whole seconds: the tracker keeps instants to the microsecond, as PostgreSQL does, and Windows clocks have finer ticks.
        var start = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.AddHours(-3).ToUnixTimeSeconds());
        var entry = new TimeEntryResponse
        {
            Id = Guid.NewGuid(), WorkspaceId = workspace, Description = "Fix login; Review PR", Start = start, Stop = start.AddHours(2),
            Shares = [new() { ProjectId = Guid.NewGuid(), Percent = 60, Description = "Fix login" }, new() { ProjectId = Guid.NewGuid(), Percent = 40, Description = "Review PR" }],
        };
        entry = entry with { ProjectId = entry.Shares[0].ProjectId };
        var client = Client(async request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/auth/login") return Json(Tokens());
            if (offline) throw new HttpRequestException("Offline");
            if (request.Method == HttpMethod.Put)
            {
                var body = JsonSerializer.Deserialize<TimeEntryRequest>(await request.Content!.ReadAsStringAsync(), SpindleApiClient.JsonOptions)!;
                puts.Add(body);
                var shares = body.Shares!.Select(s => new TimeShareResponse { ProjectId = s.ProjectId, TaskId = s.TaskId, IsBillable = s.IsBillable ?? false, Percent = s.Percent, Description = s.Description }).ToArray();
                var (kept, description) = SplitMath.Described(shares, body.Description ?? "", body.DescribeParts, entry);
                entry = entry with { Description = description, Shares = kept, Start = body.Start!.Value, Stop = body.Stop };
                return Json(entry);
            }
            if (path.EndsWith($"/time-entries/{entry.Id:D}")) return Json(entry);
            if (path == "/api/me/time-entries/current") return new(HttpStatusCode.NoContent);
            return Json(new[] { entry });
        }, new());
        await Login(client);
        var tracker = new OfflineTracker(client, directory, Guid.NewGuid());
        await tracker.LoadAsync();
        await tracker.SynchronizeAsync(workspace, start.AddDays(-1), start.AddDays(1));
        offline = true;
        // As the calendar moves an entry: its details as they are, without saying how the parts are described.
        var moved = await tracker.UpdateAsync(entry, new TimeEntryRequest
        {
            Description = entry.Description, ProjectId = entry.ProjectId, Start = start.AddMinutes(30), Stop = start.AddMinutes(150),
            Shares = entry.Shares.Select(s => new TimeShareRequest { ProjectId = s.ProjectId, Percent = s.Percent }).ToArray(),
        });
        Assert(moved.Shares.Select(s => s.Description).SequenceEqual(["Fix login", "Review PR"]) && moved.Description == "Fix login; Review PR", "Offline move lost the parts' descriptions.");
        offline = false;
        await tracker.SynchronizeAsync(workspace, start.AddDays(-1), start.AddDays(1));
        var put = puts.Single();
        Assert(tracker.PendingChanges.Count == 0 && put.DescribeParts == true && put.Shares!.Select(s => s.Description).SequenceEqual(["Fix login", "Review PR"]), "Replay did not say the parts are described.");
        Assert(entry.Shares.Select(s => s.Description).SequenceEqual(["Fix login", "Review PR"]) && entry.Start == start.AddMinutes(30), "Server lost part descriptions on replay.");
        // A new description for the whole entry is one description for all again, here as on the server.
        var shared = await tracker.UpdateAsync(entry, new TimeEntryRequest { Description = "Both", ProjectId = entry.ProjectId, Start = entry.Start, Stop = entry.Stop,
            Shares = entry.Shares.Select(s => new TimeShareRequest { ProjectId = s.ProjectId, Percent = s.Percent, Description = s.Description }).ToArray() });
        Assert(shared.Description == "Both" && shared.Shares.All(s => s.Description is null) && puts[^1].DescribeParts == false, "A new description did not replace the parts' own.");
        // Saved per project offline, the entry shows their descriptions joined at once.
        offline = true;
        var created = await tracker.CreateAsync(workspace, new CreateTimeEntryRequest
        {
            Id = Guid.NewGuid(), Description = "", Start = start.AddHours(-2), Stop = start.AddHours(-1), DescribeParts = true,
            Shares = [new() { ProjectId = Guid.NewGuid(), Percent = 50, Description = " Plan " }, new() { ProjectId = Guid.NewGuid(), Percent = 50, Description = "Build" }],
        });
        Assert(created.Description == "Plan; Build" && created.Shares[0].Description == "Plan", "Offline create did not show the parts' descriptions.");
    }
    finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}

static async Task OfflineCreate()
{
    var directory = Path.Combine(Path.GetTempPath(), "spindle-offline-test-" + Guid.NewGuid());
    try
    {
        var offline = true; var creates = 0; var user = Guid.NewGuid(); var workspace = Guid.NewGuid();
        var server = new Dictionary<Guid, TimeEntryResponse>();
        var client = Client(async request =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/auth/login") return Json(Tokens());
            if (offline) throw new HttpRequestException("Offline");
            if (request.Method == HttpMethod.Post)
            {
                creates++;
                var body = JsonSerializer.Deserialize<CreateTimeEntryRequest>(await request.Content!.ReadAsStringAsync(), SpindleApiClient.JsonOptions)!;
                var entry = new TimeEntryResponse { Id = body.Id!.Value, WorkspaceId = workspace, Description = body.Description ?? "", Start = body.Start!.Value, Stop = body.Stop, Shares = [] };
                server.TryAdd(entry.Id, entry);
                // First answer is lost after the server saved it; replay must reuse the same GUID.
                if (creates == 1) throw new HttpRequestException("Response lost");
                return Json(server[entry.Id]);
            }
            if (request.RequestUri.AbsolutePath == "/api/me/time-entries/current") return new(HttpStatusCode.NoContent);
            return Json(server.Values.ToArray());
        }, new());
        await Login(client);
        var tracker = new OfflineTracker(client, directory, user);
        await tracker.LoadAsync();
        var saved = await tracker.CreateAsync(workspace, new() { Description = "Offline work", Start = DateTimeOffset.UtcNow.AddHours(-1), Stop = DateTimeOffset.UtcNow });
        Assert(tracker.IsOffline && tracker.PendingChanges.Count == 1, "Offline create was not pending.");
        tracker = new OfflineTracker(client, directory, user);
        await tracker.LoadAsync();
        Assert(tracker.GetEntries(workspace).Single().Id == saved.Id, "Restart lost the pending entry.");
        offline = false;
        await tracker.SynchronizeAsync(workspace, saved.Start.AddDays(-1), saved.Start.AddDays(1));
        Assert(tracker.PendingChanges.Count == 1 && server.Count == 1, "Ambiguous response was not kept for reconciliation.");
        await tracker.SynchronizeAsync(workspace, saved.Start.AddDays(-1), saved.Start.AddDays(1));
        Assert(tracker.PendingChanges.Count == 0 && server.Count == 1 && tracker.GetEntries(workspace).Single().Id == saved.Id, "Replay duplicated or lost entry.");
    }
    finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}

static async Task OfflineConflict()
{
    var directory = Path.Combine(Path.GetTempPath(), "spindle-conflict-test-" + Guid.NewGuid());
    try
    {
        var offline = false; var refused = 0; var workspace = Guid.NewGuid();
        var entry = new TimeEntryResponse { Id = Guid.NewGuid(), WorkspaceId = workspace, Description = "Original", Start = DateTimeOffset.UtcNow.AddHours(-1), Stop = DateTimeOffset.UtcNow, Version = "1" };
        var client = Client(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/auth/login") return Task.FromResult(Json(Tokens()));
            if (offline) throw new HttpRequestException("Offline");
            if (request.Method != HttpMethod.Get)
            {
                // As the server does: a write whose If-Match names another version is refused with the entry as it is.
                Assert(request.Headers.IfMatch.Single().Tag == "\"1\"", "Replay did not send the version it saw.");
                refused++;
                return Task.FromResult(Json(new { type = ApiException.EntryChangedType, title = "Changed", status = 412, current = entry }, HttpStatusCode.PreconditionFailed));
            }
            if (request.RequestUri.AbsolutePath.EndsWith($"/time-entries/{entry.Id:D}")) return Task.FromResult(Json(entry));
            return Task.FromResult(request.RequestUri.AbsolutePath == "/api/me/time-entries/current" ? new HttpResponseMessage(HttpStatusCode.NoContent) : Json(new[] { entry }));
        }, new());
        await Login(client);
        var tracker = new OfflineTracker(client, directory, Guid.NewGuid());
        await tracker.LoadAsync();
        await tracker.SynchronizeAsync(workspace, entry.Start.AddDays(-1), entry.Start.AddDays(1));
        offline = true;
        await tracker.UpdateAsync(entry, new() { Description = "Local edit", Start = entry.Start, Stop = entry.Stop });
        entry = entry with { Description = "Other device edit", Version = "2" };
        offline = false;
        await tracker.SynchronizeAsync(workspace, entry.Start.AddDays(-1), entry.Start.AddDays(1));
        Assert(refused == 1 && tracker.PendingChanges.Single().Error is not null && tracker.GetEntries(workspace).Single().Description == "Local edit", "Replay silently overwrote remote or lost pending edit.");
        await tracker.DiscardPendingAsync(tracker.PendingChanges.Single().Id);
        Assert(tracker.PendingChanges.Count == 0 && tracker.GetEntries(workspace).Single().Description == "Other device edit", "Discard failed to restore server data.");
    }
    finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}

static async Task OfflineVersions()
{
    var directory = Path.Combine(Path.GetTempPath(), "spindle-versions-test-" + Guid.NewGuid());
    try
    {
        var offline = false; var version = 1; var reads = 0; var workspace = Guid.NewGuid(); var sent = new List<string>();
        var entry = new TimeEntryResponse { Id = Guid.NewGuid(), WorkspaceId = workspace, Description = "Original", Start = DateTimeOffset.UtcNow.AddHours(-1), Stop = DateTimeOffset.UtcNow, Version = "1" };
        var client = Client(async request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/auth/login") return Json(Tokens());
            if (offline) throw new HttpRequestException("Offline");
            if (request.Method == HttpMethod.Put)
            {
                var tag = request.Headers.IfMatch.Single().Tag.Trim('"');
                sent.Add(tag);
                if (tag != entry.Version) return Json(new { type = ApiException.EntryChangedType, status = 412, current = entry }, HttpStatusCode.PreconditionFailed);
                var body = JsonSerializer.Deserialize<TimeEntryRequest>(await request.Content!.ReadAsStringAsync(), SpindleApiClient.JsonOptions)!;
                entry = entry with { Description = body.Description ?? "", Version = (++version).ToString() };
                return Json(entry);
            }
            if (path.EndsWith($"/time-entries/{entry.Id:D}")) { reads++; return Json(entry); }
            if (path == "/api/me/time-entries/current") return new(HttpStatusCode.NoContent);
            return Json(new[] { entry });
        }, new());
        await Login(client);
        var tracker = new OfflineTracker(client, directory, Guid.NewGuid());
        await tracker.LoadAsync();
        await tracker.SynchronizeAsync(workspace, entry.Start.AddDays(-1), entry.Start.AddDays(1));
        offline = true;
        var first = await tracker.UpdateAsync(tracker.GetEntries(workspace).Single(), new() { Description = "First", Start = entry.Start, Stop = entry.Stop });
        await tracker.UpdateAsync(first, new() { Description = "Second", Start = entry.Start, Stop = entry.Stop });
        offline = false;
        await tracker.SynchronizeAsync(workspace, entry.Start.AddDays(-1), entry.Start.AddDays(1));
        Assert(tracker.PendingChanges.Count == 0 && entry.Description == "Second" && sent.SequenceEqual(["1", "2"]) && reads == 0,
            "Queued edits did not replay on the versions they were made on, without reading first.");
    }
    finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}

static async Task OfflineColdStart()
{
    var directory = Path.Combine(Path.GetTempPath(), "spindle-cold-test-" + Guid.NewGuid());
    try
    {
        var creates = 0; var workspace = Guid.NewGuid(); var user = Guid.NewGuid();
        var server = new List<TimeEntryResponse>();
        var client = Client(async request =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/auth/login") return Json(Tokens());
            if (request.Method == HttpMethod.Post)
            {
                creates++;
                var body = JsonSerializer.Deserialize<CreateTimeEntryRequest>(await request.Content!.ReadAsStringAsync(), SpindleApiClient.JsonOptions)!;
                var entry = new TimeEntryResponse { Id = body.Id!.Value, WorkspaceId = workspace, Start = body.Start!.Value, Stop = body.Stop, Description = body.Description ?? "" };
                server.Add(entry); return Json(entry);
            }
            return request.RequestUri.AbsolutePath == "/api/me/time-entries/current" ? new(HttpStatusCode.NoContent) : Json(server);
        }, new());
        var tracker = new OfflineTracker(client, directory, user);
        await tracker.LoadAsync();
        var entry = await tracker.CreateAsync(workspace, new() { Description = "Cold offline", Start = DateTimeOffset.UtcNow.AddHours(-1), Stop = DateTimeOffset.UtcNow });
        Assert(tracker.IsOffline && tracker.PendingChanges.Count == 1 && creates == 0, "Unauthenticated cold start rejected local edit or used network.");
        await Login(client);
        await tracker.SynchronizeAsync(workspace, entry.Start.AddDays(-1), entry.Start.AddDays(1));
        Assert(creates == 1 && tracker.PendingChanges.Count == 0, "Cold-start queue failed to sync after login.");
    }
    finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}

static async Task OfflineValidation()
{
    var directory = Path.Combine(Path.GetTempPath(), "spindle-validation-test-" + Guid.NewGuid());
    try
    {
        var client = Client(_ => throw new InvalidOperationException("Validation should not call API."), new());
        var tracker = new OfflineTracker(client, directory, Guid.NewGuid());
        var workspace = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        var invalid = new CreateTimeEntryRequest[]
        {
            new() { Start = now, Stop = now.AddSeconds(-1) },
            new() { Start = now, Stop = now.AddHours(1000) },
            new() { Start = now.AddHours(1) },
            new() { Start = now, Stop = now, TaskId = Guid.NewGuid() },
            new() { Start = now, Stop = now, Shares = [new() { Percent = 60 }, new() { Percent = 40 }] },
            new() { Start = now, Stop = now, Shares = [new() { ProjectId = Guid.NewGuid(), Percent = 50 }, new() { ProjectId = Guid.NewGuid(), Percent = 40 }] },
        };
        foreach (var request in invalid)
        {
            try { await tracker.CreateAsync(workspace, request); throw new InvalidOperationException("Invalid entry was accepted."); }
            catch (ArgumentException) { }
        }
        Assert(tracker.PendingChanges.Count == 0 && !Directory.Exists(directory), "Invalid entry was persisted.");
    }
    finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}

static async Task OfflineCreateConflict()
{
    var directory = Path.Combine(Path.GetTempPath(), "spindle-create-conflict-test-" + Guid.NewGuid());
    try
    {
        var workspace = Guid.NewGuid(); var id = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        var stored = new TimeEntryResponse { Id = id, WorkspaceId = workspace, Description = "Changed elsewhere", Start = now.AddHours(-1), Stop = now };
        var writes = 0;
        var client = Client(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/auth/login") return Task.FromResult(Json(Tokens()));
            if (request.Method == HttpMethod.Post) { writes++; return Task.FromResult(Json(stored)); }
            if (request.RequestUri.AbsolutePath.EndsWith($"/time-entries/{id:D}")) return Task.FromResult(Json(stored));
            return Task.FromResult(request.RequestUri.AbsolutePath == "/api/me/time-entries/current" ? new HttpResponseMessage(HttpStatusCode.NoContent) : Json(new[] { stored }));
        }, new());
        await Login(client);
        var tracker = new OfflineTracker(client, directory, Guid.NewGuid());
        await tracker.CreateAsync(workspace, new() { Id = id, Description = "Original create", Start = stored.Start, Stop = stored.Stop });
        Assert(writes == 1 && tracker.PendingChanges.Single().Error is not null && tracker.GetEntries(workspace).Single().Description == "Original create", "Changed server create was silently accepted.");
        await tracker.DiscardPendingAsync(tracker.PendingChanges.Single().Id);
        Assert(tracker.GetEntries(workspace).Single().Description == "Changed elsewhere", "Create conflict discard did not restore current data.");
    }
    finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}

static Task PkceVector()
{
    // RFC 7636, appendix B.
    Assert(Pkce.Challenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk") == "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", "PKCE challenge differs from RFC 7636.");
    var verifier = Pkce.NewVerifier();
    Assert(verifier.Length == 43 && verifier.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'), "Verifier is not 32 bytes of base64url without padding.");
    Assert(Pkce.NewVerifier() != verifier, "Verifiers repeat.");
    return Task.CompletedTask;
}

static Task ExternalStart()
{
    var start = ExternalSignIn.StartUri(new("https://test.spindle.invalid/"), "google", "http://127.0.0.1:50123/auth/callback", "challenge", "Europe/Zagreb");
    Assert(start.AbsoluteUri == "https://test.spindle.invalid/api/auth/external/google/start?redirectUri=http%3A%2F%2F127.0.0.1%3A50123%2Fauth%2Fcallback&codeChallenge=challenge&timeZone=Europe%2FZagreb&acceptTerms=true",
        "Start URL differs: " + start.AbsoluteUri);
    var invited = ExternalSignIn.StartUri(new("https://test.spindle.invalid/"), "apple", "http://127.0.0.1:1/auth/callback", "c", "UTC", "a b+c");
    Assert(invited.AbsoluteUri.EndsWith("&acceptTerms=true&invitation=a%20b%2Bc", StringComparison.Ordinal), "Invitation missing: " + invited.AbsoluteUri);
    return Task.CompletedTask;
}

static Task ExternalCallbacks()
{
    Assert(ExternalCallback.Parse("/auth/callback?code=a%2Bb%3D") == new ExternalCallback("a+b=", null), "Code not read.");
    Assert(ExternalCallback.Parse("/auth/callback?error=cancelled") == new ExternalCallback(null, "cancelled"), "Cancel not read.");
    Assert(ExternalCallback.Parse("/auth/callback") == new ExternalCallback(null, "missing"), "A callback without a code must fail rather than wait.");
    Assert(ExternalCallback.Parse("/favicon.ico") is null && ExternalCallback.Parse("/auth/callback/x?code=a") is null, "Other paths were taken for the callback.");
    return Task.CompletedTask;
}

static async Task LoopbackRoundTrip()
{
    using var loopback = new LoopbackCallback();
    Assert(System.Text.RegularExpressions.Regex.IsMatch(loopback.RedirectUri, @"^http://127\.0\.0\.1:\d+/auth/callback$"), "Redirect URI is not what the server accepts: " + loopback.RedirectUri);
    var waiting = loopback.WaitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);
    var port = new Uri(loopback.RedirectUri).Port;
    // A spare connection that never sends anything, as browsers open, must not hold up the real request.
    using var idle = new System.Net.Sockets.TcpClient();
    await idle.ConnectAsync(IPAddress.Loopback, port);
    using var browser = new HttpClient();
    var favicon = await browser.GetAsync($"http://127.0.0.1:{port}/favicon.ico");
    Assert(favicon.StatusCode == HttpStatusCode.NotFound, "Other requests should get 404.");
    var page = await browser.GetAsync(loopback.RedirectUri + "?code=x");
    Assert(page.IsSuccessStatusCode && (await page.Content.ReadAsStringAsync()).Contains("You can close this tab and go back to Spindle."), "Browser wasn't answered.");
    Assert(await waiting == new ExternalCallback("x", null), "Code not handed back.");
    try { await browser.GetAsync(loopback.RedirectUri + "?code=y"); throw new InvalidOperationException("Still listening after the callback."); }
    catch (HttpRequestException) { }
}

static async Task ExternalSignInFlow()
{
    string? challenge = null;
    var store = new MemoryStore();
    var exchanged = new List<JsonElement>();
    var problem = false;
    var client = Client(async request =>
    {
        Assert(request.RequestUri!.AbsolutePath == "/api/auth/external/exchange" && request.Method == HttpMethod.Post, "Unexpected request " + request.RequestUri);
        var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync()).RootElement.Clone();
        exchanged.Add(body);
        if (problem) return Json(new { type = "about:blank", title = "This sign-in has expired. Please try again.", status = 401 }, HttpStatusCode.Unauthorized);
        return Json(new { accessToken = "access", accessTokenExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15), refreshToken = "external-refresh", refreshTokenExpiresAt = DateTimeOffset.UtcNow.AddDays(90), newAccount = true });
    }, store);
    // Plays the browser: reads the start URL and comes back to the loopback as the server would.
    Action<Uri> Browser(string answer) => start =>
    {
        var query = start.Query.TrimStart('?').Split('&').Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));
        Assert(start.AbsolutePath == "/api/auth/external/google/start" && query["timeZone"] == "Europe/Zagreb" && query["acceptTerms"] == "true", "Start URL is wrong: " + start);
        challenge = query["codeChallenge"];
        _ = Task.Run(() => new HttpClient().GetAsync(query["redirectUri"] + answer));
    };

    var created = await ExternalSignIn.RunAsync(client, "google", "Europe/Zagreb", "Test PC", null, Browser("?code=abc"));
    var sent = exchanged.Single();
    Assert(created == true && client.IsAuthenticated && store.Session?.RefreshToken == "external-refresh", "External sign-in didn't keep the session.");
    Assert(sent.GetProperty("code").GetString() == "abc" && sent.GetProperty("deviceName").GetString() == "Test PC"
        && Pkce.Challenge(sent.GetProperty("codeVerifier").GetString()!) == challenge, "Exchange didn't send the code with the challenge's verifier.");

    var cancelled = await ExternalSignIn.RunAsync(client, "google", "Europe/Zagreb", "Test PC", null, Browser("?error=cancelled"));
    Assert(cancelled is null && exchanged.Count == 1, "Turning back in the browser should end quietly, without an exchange.");

    problem = true;
    try { await ExternalSignIn.RunAsync(client, "google", "Europe/Zagreb", "Test PC", null, Browser("?code=old")); throw new InvalidOperationException("Expected failure."); }
    catch (ApiException ex) { Assert(ex.Message == "This sign-in has expired. Please try again.", "Problem title not shown: " + ex.Message); }

    using var cancel = new CancellationTokenSource();
    var waiting = ExternalSignIn.RunAsync(client, "google", "Europe/Zagreb", "Test PC", null, _ => cancel.CancelAfter(50), cancel.Token);
    try { await waiting; throw new InvalidOperationException("Cancel didn't stop waiting."); }
    catch (OperationCanceledException) { }
}

sealed class MemoryStore : ISessionStore
{
    public StoredSession? Session { get; set; }
    public Task<StoredSession?> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(Session);
    public Task SaveAsync(StoredSession session, CancellationToken cancellationToken = default) { Session = session; return Task.CompletedTask; }
    public Task ClearAsync(CancellationToken cancellationToken = default) { Session = null; return Task.CompletedTask; }
}
sealed class FakeHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
}
