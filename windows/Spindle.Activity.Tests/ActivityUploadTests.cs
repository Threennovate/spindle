using System.Text.Json;
using Spindle.Activity;

namespace Spindle.Activity.Tests;

/// <summary>The Mac's cases (SpindleKitTests/ActivitySyncTests.swift), so both apps name and send sessions the same way.</summary>
public sealed class RepositoryIdentityTests
{
    [Theory]
    [InlineData("https://github.com/Acme/atlas.git")]
    [InlineData("https://github.com/Acme/atlas")]
    [InlineData("git@github.com:Acme/atlas.git")]
    [InlineData("ssh://git@github.com:22/Acme/atlas.git/")]
    [InlineData("https://x-access-token:ghp_secret@github.com/Acme/atlas.git")]
    [InlineData("https://ivan@github.com/acme/atlas")]
    [InlineData("  git@github.com:acme/atlas.git\n")]
    [InlineData("HTTPS://GitHub.com/ACME/Atlas.GIT")]
    public void An_origin_is_the_same_over_ssh_and_https_without_credentials(string url) =>
        Assert.Equal("origin:github.com/acme/atlas", RepositoryResolver.OriginIdentity(url));

    [Fact]
    public void Nested_groups_are_kept_and_ports_and_queries_dropped()
    {
        Assert.Equal("origin:gitlab.example.com/group/sub/repo", RepositoryResolver.OriginIdentity("https://token@gitlab.example.com:8443/group/sub/repo.git?x=1"));
        Assert.Equal("origin:git.example.com/team/repo", RepositoryResolver.OriginIdentity("user:password@git.example.com:team/repo"));
    }

    [Theory]
    [InlineData("/Users/ivan/git/repo.git")]
    [InlineData("file:///Users/ivan/repo")]
    [InlineData("FILE:///C:/code/repo")]
    [InlineData("../other")]
    [InlineData("C:/code/repo")]
    [InlineData("C:\\code\\repo.git")]
    [InlineData("c:\\code\\repo")]
    [InlineData("\\\\server\\share\\repo.git")]
    [InlineData("//server/share/repo.git")]
    [InlineData("https://github.com/")]
    [InlineData("")]
    [InlineData("git@host:my repo.git")]
    public void An_origin_that_is_a_local_path_or_unusable_is_no_origin(string url) =>
        Assert.Null(RepositoryResolver.OriginIdentity(url));

    [Fact]
    public void An_origin_longer_than_the_server_takes_is_no_origin() =>
        Assert.Null(RepositoryResolver.OriginIdentity("https://example.com/" + new string('a', 300)));

    [Fact]
    public void A_path_is_named_by_the_start_of_its_hash_and_says_nothing_of_the_disk()
    {
        var identity = RepositoryResolver.PathIdentity(@"C:\Users\ivan\Repos\secret-client");
        Assert.Matches("^path:[0-9a-f]{32}$", identity);
        Assert.DoesNotContain("ivan", identity);
        Assert.NotEqual(identity, RepositoryResolver.PathIdentity(@"C:\Users\ivan\Repos\other"));
        // The first 32 hex digits of the SHA-256 of "abc", as the Mac and the server see them.
        Assert.Equal("path:ba7816bf8f01cfea414140de5dae2223", RepositoryResolver.PathIdentity("abc"));
    }

    [Fact]
    public void A_repository_with_a_local_origin_is_named_by_its_path()
    {
        var directory = Path.Combine(Path.GetTempPath(), "spindle-identity-" + Guid.NewGuid());
        try
        {
            Directory.CreateDirectory(Path.Combine(directory, ".git"));
            File.WriteAllText(Path.Combine(directory, ".git", "config"), "[remote \"origin\"]\n url = C:\\srv\\git\\secret.git\n");
            Assert.Equal(RepositoryResolver.PathIdentity(directory), RepositoryResolver.Resolve(directory)?.Identity);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void A_folder_outside_git_has_no_repository()
    {
        var directory = Path.Combine(Path.GetTempPath(), "spindle-scratch-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try { Assert.Null(RepositoryResolver.Resolve(directory)); }
        finally { Directory.Delete(directory, true); }
    }
}

public sealed class ActivityUploadTests
{
    private const long Start = 1_790_000_000_000;
    private static readonly AgentRepository Atlas = new("origin:github.com/acme/atlas", "atlas");

    private static LocalAgentSession Session(string id, (double Minutes, bool Touch)[] events, string title = "Fix the login page", AgentSource source = AgentSource.Claude, string? parent = null) =>
        new(id, source, parent, @"C:\Users\ivan\code\atlas\sub", title, ["main", "feat/x"], events.Select(e => new AgentEvent(Start + (long)(e.Minutes * 60_000), e.Touch)).ToArray());

    [Fact]
    public void A_session_goes_as_prompt_times_and_runs_without_its_path()
    {
        var upload = AgentSessionUpload.Create(Session("claude:1", [(0, true), (1, false), (2, false), (10, true), (11, false)]), Atlas, titles: false)!;
        Assert.Equal([Start, Start + 600_000], upload.Touches);
        Assert.Equal([new UploadRun(Start, Start + 120_000), new UploadRun(Start + 600_000, Start + 660_000)], upload.Runs);
        Assert.Equal(new UploadRepository("origin:github.com/acme/atlas", "atlas"), upload.Repository);
        Assert.Null(upload.Title);
        Assert.Equal(["feat/x", "main"], upload.Branches);
        Assert.Equal("claude", upload.Source);
        var json = JsonSerializer.Serialize(upload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.DoesNotContain("Users", json);
        Assert.DoesNotContain("ivan", json);
        Assert.Contains("\"sessionId\":\"claude:1\"", json);
    }

    [Fact]
    public void Events_exactly_three_minutes_apart_are_one_run_and_more_are_two()
    {
        var together = AgentSessionUpload.Create(Session("claude:1", [(0, true), (3, false)]), Atlas, false)!;
        Assert.Equal([new UploadRun(Start, Start + 180_000)], together.Runs);
        var apart = AgentSessionUpload.Create(Session("claude:1", [(0, true), (3.01, false)]), Atlas, false)!;
        Assert.Equal(2, apart.Runs.Count);
    }

    [Fact]
    public void Runs_come_from_all_events_in_order_and_a_lone_event_is_an_empty_run()
    {
        var upload = AgentSessionUpload.Create(Session("codex:1", [(20, false), (0, true), (10, false)], source: AgentSource.Codex), null, false)!;
        Assert.Equal([new UploadRun(Start, Start), new UploadRun(Start + 600_000, Start + 600_000), new UploadRun(Start + 1_200_000, Start + 1_200_000)], upload.Runs);
        Assert.Equal("codex", upload.Source);
        Assert.Null(upload.Repository);
    }

    [Fact]
    public void A_subagent_has_runs_and_a_parent_and_no_touches()
    {
        var upload = AgentSessionUpload.Create(Session("codex:2", [(0, false), (1, false)], source: AgentSource.Codex, parent: "codex:1"), Atlas, false)!;
        Assert.Empty(upload.Touches);
        Assert.Equal("codex:1", upload.ParentSessionId);
        Assert.Single(upload.Runs);
    }

    [Fact]
    public void Titles_go_only_when_allowed()
    {
        Assert.Equal("Fix the login page", AgentSessionUpload.Create(Session("claude:1", [(0, true)]), Atlas, titles: true)!.Title);
        Assert.Null(AgentSessionUpload.Create(Session("claude:1", [(0, true)], title: "  "), Atlas, titles: true)!.Title);
    }

    [Fact]
    public void Long_names_and_titles_are_cut_to_what_the_server_takes_and_a_session_without_events_is_not_sent()
    {
        var upload = AgentSessionUpload.Create(Session("claude:1", [(0, true)], title: string.Concat(Enumerable.Repeat("é🦊", 100))),
            new("origin:x/y", string.Concat(Enumerable.Repeat("🦊", 150))), titles: true)!;
        Assert.True(upload.Repository!.Name.Length <= 200);
        Assert.True(upload.Title!.Length <= 200);
        Assert.False(char.IsHighSurrogate(upload.Title[^1]));
        Assert.Null(AgentSessionUpload.Create(Session("claude:1", []), Atlas, false));
        Assert.Null(AgentSessionUpload.Create(Session(new string('x', 129), [(0, true)]), Atlas, false));
        Assert.Equal("Repository", AgentSessionUpload.Create(Session("claude:1", [(0, true)]), new("path:0123456789abcdef", ""), false)!.Repository!.Name);
    }

    // The uploader

    private sealed class Logs(params LocalAgentSession[] sessions)
    {
        public List<LocalAgentSession> Sessions { get; } = [.. sessions];
        public List<(DateTimeOffset From, DateTimeOffset? Changed)> Reads { get; } = [];
        public List<string[]> Sent { get; } = [];
        public Task<IReadOnlyList<(LocalAgentSession Session, AgentRepository? Repository)>> ReadAsync(DateTimeOffset from, DateTimeOffset? changed, CancellationToken _)
        {
            Reads.Add((from, changed));
            return Task.FromResult<IReadOnlyList<(LocalAgentSession, AgentRepository?)>>(Sessions.Select(s => (s, (AgentRepository?)Atlas)).ToArray());
        }

        /// <summary>Makes a send fail: the exception to throw for a batch, or null to take it.</summary>
        public Func<IReadOnlyList<AgentSessionUpload>, Exception?>? Fail { get; set; }
        public List<string[]> Attempts { get; } = [];

        public Task SendAsync(IReadOnlyList<AgentSessionUpload> batch, CancellationToken _)
        {
            Attempts.Add(batch.Select(s => s.SessionId).ToArray());
            if (Fail?.Invoke(batch) is { } failure) throw failure;
            Sent.Add(batch.Select(s => s.SessionId).ToArray());
            return Task.CompletedTask;
        }
    }

    private static readonly DateTimeOffset Since = DateTimeOffset.FromUnixTimeSeconds(1_780_000_000);
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeMilliseconds(Start);

    private static Task<AgentUploadResult> Upload(AgentActivityUploader uploader, Logs logs, bool titles = false, DateTimeOffset? at = null, DateTimeOffset? since = null) =>
        uploader.UploadAsync(since ?? Since, titles, at ?? Now, logs.ReadAsync, logs.SendAsync, TestContext.Current.CancellationToken);

    /// <summary>What the uploader keeps in its file.</summary>
    private static JsonElement Saved(string file) => JsonDocument.Parse(File.ReadAllText(file)).RootElement;

    private static string TempFile() => Path.Combine(Path.GetTempPath(), $"uploads-{Guid.NewGuid()}", "uploads.json");

    [Fact]
    public async Task A_source_turned_on_or_added_by_an_update_reads_the_history_again()
    {
        var file = TempFile();
        try
        {
            var logs = new Logs();
            HashSet<AgentSource> two = [AgentSource.Claude, AgentSource.Codex], three = [.. two, AgentSource.Copilot];
            Task Look(IReadOnlySet<AgentSource> sources, int minutes) => new AgentActivityUploader(file).UploadAsync(Since, false,
                Now.AddMinutes(minutes), logs.ReadAsync, logs.SendAsync, TestContext.Current.CancellationToken, sources);
            await Look(two, 0);
            // As an earlier version left it: no sources, so Claude Code and Codex.
            File.WriteAllText(file, File.ReadAllText(file).Replace("\"Sources\":[\"claude\",\"codex\"]", "\"Sources\":null"));
            Assert.Equal(JsonValueKind.Null, Saved(file).GetProperty("Sources").ValueKind);
            await Look(two, 5);
            await Look(three, 10);
            await Look(three, 15);
            await Look(new HashSet<AgentSource> { AgentSource.Claude }, 20);
            Assert.Equal([null, Now.AddMinutes(-1), null, Now.AddMinutes(9), Now.AddMinutes(14)], logs.Reads.Select(r => r.Changed));
        }
        finally { Directory.Delete(Path.GetDirectoryName(file)!, true); }
    }

    [Fact]
    public async Task Only_changed_sessions_go_again_until_the_settings_change()
    {
        var file = TempFile();
        try
        {
            var logs = new Logs(Session("claude:1", [(0, true)]), Session("claude:2", [(5, true)]));
            await Upload(new AgentActivityUploader(file), logs);
            await Upload(new AgentActivityUploader(file), logs, at: Now.AddMinutes(1));
            logs.Sessions[1] = Session("claude:2", [(5, true), (10, true)]);
            // A new launch reads what was sent from the file.
            await Upload(new AgentActivityUploader(file), logs, at: Now.AddMinutes(2));
            // Allowing titles sends everything again, and so does sync turned on again.
            await Upload(new AgentActivityUploader(file), logs, titles: true, at: Now.AddMinutes(3));
            await Upload(new AgentActivityUploader(file), logs, titles: true, at: Now.AddMinutes(4), since: Since.AddDays(1));
            Assert.Equal<string[]>([["claude:1", "claude:2"], ["claude:2"], ["claude:1", "claude:2"], ["claude:1", "claude:2"]], logs.Sent);
        }
        finally { Directory.Delete(Path.GetDirectoryName(file)!, true); }
    }

    private static Exception? RefuseB(IReadOnlyList<AgentSessionUpload> batch) =>
        batch.Any(s => s.SessionId == "claude:B") ? new AgentActivityUploadRejected("400") : null;

    [Fact]
    public async Task A_refused_batch_goes_one_session_at_a_time_and_the_refused_one_waits_until_it_changes()
    {
        var file = TempFile();
        try
        {
            var logs = new Logs(Session("claude:A", [(1, true)]), Session("claude:B", [(2, true)]), Session("claude:C", [(3, true)]));
            logs.Fail = RefuseB;
            var result = await Upload(new AgentActivityUploader(file), logs);
            Assert.Equal(new AgentUploadResult(2, 1), result);
            Assert.Equal<string[]>([["claude:A", "claude:B", "claude:C"], ["claude:A"], ["claude:B"], ["claude:C"]], logs.Attempts);

            // Handled, so nothing goes while nothing changed (the look still counted).
            logs.Attempts.Clear();
            Assert.Equal(new AgentUploadResult(0, 0), await Upload(new AgentActivityUploader(file), logs, at: Now.AddMinutes(6)));
            Assert.Empty(logs.Attempts);
            Assert.Equal(Now.AddMinutes(6), Saved(file).GetProperty("ScannedAt").GetDateTimeOffset());

            // B changes: it is sent again, and refused again.
            logs.Sessions[1] = Session("claude:B", [(2, true), (9, true)]);
            Assert.Equal(new AgentUploadResult(0, 1), await Upload(new AgentActivityUploader(file), logs, at: Now.AddMinutes(12)));
            Assert.Equal<string[]>([["claude:B"]], logs.Attempts);
        }
        finally { Directory.Delete(Path.GetDirectoryName(file)!, true); }
    }

    [Fact]
    public async Task Batches_after_a_refused_one_are_still_sent()
    {
        var logs = new Logs(Enumerable.Range(0, 150).Select(i => Session($"claude:{i:D3}", [(150 - i, true)])).ToArray());
        logs.Sessions[0] = Session("claude:B", [(-500, true)]);
        logs.Fail = RefuseB;
        var result = await Upload(new AgentActivityUploader(null), logs);
        Assert.Equal(new AgentUploadResult(149, 1), result);
        // B is the oldest, so it sits in the first batch; the second batch of 50 went whole.
        Assert.Equal(50, logs.Sent[^1].Length);
    }

    [Fact]
    public async Task Another_error_while_sending_one_at_a_time_stops_the_upload_and_keeps_the_look_open()
    {
        var file = TempFile();
        try
        {
            var logs = new Logs(Session("claude:A", [(1, true)]), Session("claude:B", [(2, true)]), Session("claude:C", [(3, true)]));
            logs.Fail = batch => batch.Any(s => s.SessionId == "claude:B") ? batch.Count > 1 ? new AgentActivityUploadRejected("400") : new HttpRequestException("offline") : null;
            await Assert.ThrowsAsync<HttpRequestException>(() => Upload(new AgentActivityUploader(file), logs));
            // A went; B and C are not recorded, and the next look reads from before this one.
            var uploader = new AgentActivityUploader(file);
            Assert.Equal(["claude:A"], Saved(file).GetProperty("Sent").EnumerateObject().Select(p => p.Name));
            Assert.Equal(JsonValueKind.Null, Saved(file).GetProperty("ScannedAt").ValueKind);
            logs.Fail = null;
            logs.Attempts.Clear();
            await Upload(uploader, logs, at: Now.AddMinutes(5));
            Assert.Equal<string[]>([["claude:B", "claude:C"]], logs.Attempts);
        }
        finally { Directory.Delete(Path.GetDirectoryName(file)!, true); }
    }

    [Fact]
    public async Task A_new_parser_version_sends_everything_again()
    {
        var file = TempFile();
        try
        {
            var logs = new Logs(Session("claude:1", [(0, true)]));
            await Upload(new AgentActivityUploader(file), logs);
            File.WriteAllText(file, File.ReadAllText(file).Replace($"\"ParserVersion\":{AgentLogParser.Version}", "\"ParserVersion\":0"));
            await Upload(new AgentActivityUploader(file), logs, at: Now.AddMinutes(1));
            Assert.Equal(2, logs.Sent.Count);
        }
        finally { Directory.Delete(Path.GetDirectoryName(file)!, true); }
    }

    [Fact]
    public async Task Sessions_go_in_batches_of_a_hundred_oldest_first()
    {
        var logs = new Logs(Enumerable.Range(0, 250).Select(i => Session($"claude:{i:D3}", [(250 - i, true)])).ToArray());
        await Upload(new AgentActivityUploader(null), logs);
        Assert.Equal([100, 100, 50], logs.Sent.Select(b => b.Length));
        Assert.Equal("claude:249", logs.Sent[0][0]);
        Assert.Equal("claude:000", logs.Sent[2][^1]);
    }

    [Fact]
    public async Task A_failed_batch_is_tried_again_and_what_went_is_not_sent_twice()
    {
        var file = TempFile();
        try
        {
            var logs = new Logs(Enumerable.Range(0, 150).Select(i => Session($"claude:{i:D3}", [(150 - i, true)])).ToArray());
            var uploader = new AgentActivityUploader(file);
            var calls = 0;
            await Assert.ThrowsAsync<HttpRequestException>(() => uploader.UploadAsync(Since, false, Now, logs.ReadAsync, (batch, ct) =>
                ++calls == 2 ? throw new HttpRequestException("offline") : logs.SendAsync(batch, ct), TestContext.Current.CancellationToken));
            Assert.Single(logs.Sent);
            await Upload(new AgentActivityUploader(file), logs, at: Now.AddMinutes(5));
            Assert.Equal([100, 50], logs.Sent.Select(b => b.Length));
            // The failed look didn't count: the next one still reads from before it.
            Assert.Null(logs.Reads[1].Changed);
        }
        finally { Directory.Delete(Path.GetDirectoryName(file)!, true); }
    }

    [Fact]
    public async Task The_first_look_reads_ninety_days_and_later_ones_only_changed_logs_from_the_same_start()
    {
        var logs = new Logs(Session("claude:1", [(0, true)]));
        var uploader = new AgentActivityUploader(null);
        await Upload(uploader, logs);
        await Upload(uploader, logs, at: Now.AddDays(10));
        Assert.Equal(Now - TimeSpan.FromDays(90), logs.Reads[0].From);
        Assert.Null(logs.Reads[0].Changed);
        // A session stays whole as time passes; a minute's overlap catches logs written during the last look.
        Assert.Equal(Now - TimeSpan.FromDays(90), logs.Reads[1].From);
        Assert.Equal(Now.AddMinutes(-1), logs.Reads[1].Changed);
    }

    [Fact]
    public async Task Nothing_older_than_the_servers_retention_is_read()
    {
        var logs = new Logs();
        var uploader = new AgentActivityUploader(null);
        await Upload(uploader, logs);
        await Upload(uploader, logs, at: Now.AddDays(500));
        Assert.Equal(Now.AddDays(100), logs.Reads[1].From);
    }

    [Fact]
    public async Task Reset_forgets_what_was_sent()
    {
        var file = TempFile();
        try
        {
            var logs = new Logs(Session("claude:1", [(0, true)]));
            var uploader = new AgentActivityUploader(file);
            await Upload(uploader, logs);
            Assert.True(File.Exists(file));
            uploader.Reset();
            Assert.False(File.Exists(file));
            await Upload(uploader, logs, at: Now.AddMinutes(1));
            Assert.Equal(2, logs.Sent.Count);
            Assert.Null(logs.Reads[1].Changed);
        }
        finally { Directory.Delete(Path.GetDirectoryName(file)!, true); }
    }

    // Reading whole sessions from the logs

    [Fact]
    public async Task A_subagent_whose_parents_log_did_not_change_still_belongs_to_the_parents_repository()
    {
        var home = Path.Combine(Path.GetTempPath(), "spindle-parent-" + Guid.NewGuid());
        var token = TestContext.Current.CancellationToken;
        try
        {
            var atlas = Path.Combine(home, "atlas");
            var elsewhere = Path.Combine(home, "elsewhere");
            Directory.CreateDirectory(Path.Combine(atlas, ".git"));
            Directory.CreateDirectory(elsewhere);
            var logs = Path.Combine(home, ".codex", "sessions", "2026", "10", "10");
            Directory.CreateDirectory(logs);
            string Line(object record) => JsonSerializer.Serialize(record) + "\n";
            var parent = Path.Combine(logs, "rollout-2026-10-10T09-00-00-parent.jsonl");
            var child = Path.Combine(logs, "rollout-2026-10-10T09-05-00-child.jsonl");
            await File.WriteAllTextAsync(parent, Line(new { type = "session_meta", payload = new { id = "parent", cwd = atlas } })
                + Line(new { type = "event_msg", timestamp = "2026-10-10T09:00:00Z", payload = new { type = "task_started" } }), token);
            await File.WriteAllTextAsync(child, Line(new { type = "session_meta", payload = new { id = "child", cwd = elsewhere, parent_thread_id = "parent" } })
                + Line(new { type = "response_item", timestamp = "2026-10-10T09:05:00Z", payload = new { type = "message", role = "assistant" } }), token);
            var service = new AgentActivityService(home);
            var from = DateTimeOffset.Parse("2026-10-01T00:00:00Z");
            var all = await service.ReadSessionsAsync(from, cancellationToken: token);
            Assert.Equal(["codex:child", "codex:parent"], all.Select(s => s.Session.Id).Order());

            // Only the child's log changed: only the child comes, and in the parent's repository.
            File.SetLastWriteTimeUtc(parent, DateTime.UtcNow.AddHours(-2));
            File.SetLastWriteTimeUtc(child, DateTime.UtcNow);
            var changed = await new AgentActivityService(home).ReadSessionsAsync(from, DateTimeOffset.UtcNow.AddHours(-1), token);
            var (session, repository) = Assert.Single(changed);
            Assert.Equal("codex:child", session.Id);
            Assert.Equal(RepositoryResolver.Resolve(atlas), repository);
            Assert.Equal("atlas", repository?.Name);
        }
        finally { Directory.Delete(home, true); }
    }

    [Fact]
    public async Task Sessions_are_read_whole_and_only_the_logs_of_changed_sessions_again()
    {
        var home = Path.Combine(Path.GetTempPath(), "spindle-upload-" + Guid.NewGuid());
        var token = TestContext.Current.CancellationToken;
        try
        {
            var project = Path.Combine(home, ".claude", "projects", "atlas");
            Directory.CreateDirectory(Path.Combine(project, "one", "subagents"));
            string Record(string session, string time, bool sidechain = false) => JsonSerializer.Serialize(new
            {
                type = "user", sessionId = session, cwd = home, timestamp = time, isSidechain = sidechain, message = new { content = "Do the thing" },
            }) + "\n";
            var main = Path.Combine(project, "one.jsonl");
            var sub = Path.Combine(project, "one", "subagents", "agent-a.jsonl");
            var other = Path.Combine(project, "two.jsonl");
            await File.WriteAllTextAsync(main, Record("one", "2026-10-10T09:00:00Z") + Record("one", "2026-10-10T09:30:00Z"), token);
            await File.WriteAllTextAsync(sub, Record("one", "2026-10-10T09:10:00Z", sidechain: true), token);
            await File.WriteAllTextAsync(other, Record("two", "2026-10-10T10:00:00Z"), token);
            var service = new AgentActivityService(home);
            var from = DateTimeOffset.Parse("2026-10-01T00:00:00Z");
            var all = await service.ReadSessionsAsync(from, cancellationToken: token);
            Assert.Equal(["claude:one", "claude:two"], all.Select(s => s.Session.Id).Order());
            Assert.Equal(3, all.Single(s => s.Session.Id == "claude:one").Session.Events.Count);

            // Only the subagent's log changed: the session still comes whole, the other not at all.
            var old = DateTime.UtcNow.AddHours(-2);
            foreach (var file in new[] { main, other }) File.SetLastWriteTimeUtc(file, old);
            File.SetLastWriteTimeUtc(sub, DateTime.UtcNow);
            var changed = await service.ReadSessionsAsync(from, DateTimeOffset.UtcNow.AddHours(-1), token);
            Assert.Equal(["claude:one"], changed.Select(s => s.Session.Id));
            Assert.Equal(3, changed[0].Session.Events.Count);

            // Events before the start are not part of what is uploaded.
            var late = await service.ReadSessionsAsync(DateTimeOffset.Parse("2026-10-10T09:20:00Z"), cancellationToken: token);
            Assert.Equal(["claude:one", "claude:two"], late.Select(s => s.Session.Id).Order());
            Assert.Single(late.Single(s => s.Session.Id == "claude:one").Session.Events);
        }
        finally { Directory.Delete(home, true); }
    }
}
