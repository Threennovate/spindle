using System.Text;
using System.Text.Json;
using Spindle.Activity;

namespace Spindle.Activity.Tests;

public sealed class ParserTests
{
    private static void Line(AgentLogParser parser, object record) => parser.Consume(JsonSerializer.SerializeToUtf8Bytes(record));
    private static AgentLogParser Codex(string? parent = null)
    {
        var parser = new AgentLogParser(AgentSource.Codex);
        Line(parser, new { type = "session_meta", payload = new { id = "session", cwd = "/repo", parent_thread_id = parent } });
        Line(parser, new { type = "event_msg", timestamp = "2026-10-10T09:00:00Z", payload = new { type = "task_started" } });
        return parser;
    }

    [Fact]
    public void Spindle_only_turn_is_removed_and_other_work_restores_touch()
    {
        var parser = Codex();
        Line(parser, new { type = "response_item", timestamp = "2026-10-10T09:00:01Z", payload = new {
            type = "custom_tool_call", name = "exec", input = "const r = await tools.mcp__spindle__list_entries({}); text(r);" } });
        Assert.False(parser.Sessions.Single().Events[0].IsTouch);
        Line(parser, new { type = "response_item", timestamp = "2026-10-10T09:00:02Z", payload = new {
            type = "function_call", name = "exec_command" } });
        Assert.True(parser.Sessions.Single().Events[0].IsTouch);
    }

    [Fact]
    public void Subagent_task_started_produces_no_touch()
    {
        var parser = Codex("root");
        Assert.Equal("codex:root", parser.Sessions.Single().ParentId);
        Assert.Empty(parser.Sessions.Single().Events);
    }

    [Fact]
    public void Claude_tool_results_sidechains_and_metadata_are_not_prompts()
    {
        var parser = new AgentLogParser(AgentSource.Claude);
        Line(parser, new { type = "user", sessionId = "a", cwd = "/repo", timestamp = "2026-10-10T09:00:00Z", message = new { content = "Implement this\nsecret prompt body" } });
        Line(parser, new { type = "user", sessionId = "a", cwd = "/repo", timestamp = "2026-10-10T09:01:00Z", message = new { content = new[] { new { type = "tool_result", text = "private result" } } } });
        Line(parser, new { type = "user", sessionId = "a", cwd = "/wrong", isSidechain = true, timestamp = "2026-10-10T09:02:00Z", message = new { content = "private agent prompt" } });
        Line(parser, new { type = "user", sessionId = "a", cwd = "/repo", isMeta = true, timestamp = "2026-10-10T09:03:00Z", message = new { content = "system" } });
        var session = parser.Sessions.Single();
        Assert.Single(session.Events, e => e.IsTouch);
        Assert.Equal("/repo", session.Cwd);
        Assert.Equal("Implement this", session.Title);
        Assert.DoesNotContain("secret prompt", JsonSerializer.Serialize(session));
        Assert.DoesNotContain("private result", JsonSerializer.Serialize(session));
    }

    [Fact]
    public async Task Oversized_lines_and_live_partial_tails_do_not_break_next_record()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(new string('a', 30) + "\n{}\npartial"));
        var lines = new List<string>(); var skipped = 0;
        await AgentActivityService.ReadLinesAsync(stream, line => lines.Add(Encoding.UTF8.GetString(line.Span)),
            () => skipped++, TestContext.Current.CancellationToken, maximumLineBytes: 10);
        Assert.Equal(1, skipped);
        Assert.Equal(new[] { "{}" }, lines);
    }

    [Theory]
    [InlineData("https://user:password@GitHub.com:443/Acme/Repo.git", "origin:github.com/acme/repo")]
    [InlineData("git@GitHub.com:Acme/Repo.git", "origin:github.com/acme/repo")]
    [InlineData("file:///Users/private/repo", null)]
    [InlineData("C:\\private\\repo", null)]
    public void Repository_identity_strips_credentials_and_local_paths(string origin, string? expected) =>
        Assert.Equal(expected, RepositoryResolver.OriginIdentity(origin));

    [Fact]
    public void Worktree_and_main_repository_resolve_to_the_same_identity()
    {
        var directory = Path.Combine(Path.GetTempPath(), "spindle-activity-" + Guid.NewGuid());
        var main = Path.Combine(directory, "atlas"); var work = Path.Combine(directory, "work");
        var git = Path.Combine(main, ".git"); var workGit = Path.Combine(git, "worktrees", "work");
        try
        {
            Directory.CreateDirectory(workGit); Directory.CreateDirectory(work);
            File.WriteAllText(Path.Combine(git, "config"), "[remote \"origin\"]\n url = git@github.com:acme/atlas.git\n");
            File.WriteAllText(Path.Combine(work, ".git"), "gitdir: " + workGit);
            File.WriteAllText(Path.Combine(workGit, "commondir"), "../..");
            Assert.Equal(RepositoryResolver.Resolve(main), RepositoryResolver.Resolve(work));
            Assert.Equal("atlas", RepositoryResolver.Resolve(work)?.Name);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Service_reads_only_requested_activity_and_propagates_cancellation()
    {
        var directory = Path.Combine(Path.GetTempPath(), "spindle-logs-" + Guid.NewGuid());
        try
        {
            var logs = Path.Combine(directory, ".codex", "sessions"); Directory.CreateDirectory(logs);
            var records = new object[] {
                new { type = "session_meta", payload = new { id = "a", cwd = directory } },
                new { type = "event_msg", timestamp = "2026-10-10T09:00:00Z", payload = new { type = "task_started" } },
                new { type = "response_item", timestamp = "2026-10-10T09:01:00Z", payload = new { type = "message", role = "assistant" } } };
            await File.WriteAllTextAsync(Path.Combine(logs, "rollout-test.jsonl"), string.Join('\n', records.Select(r => JsonSerializer.Serialize(r))) + "\n", TestContext.Current.CancellationToken);
            var service = new AgentActivityService(directory);
            Assert.True(service.HasLogs);
            var from = DateTimeOffset.Parse("2026-10-10T09:00:00Z");
            var snapshot = await service.ReadAsync(from, from.AddHours(1), TestContext.Current.CancellationToken);
            Assert.Single(snapshot.Sessions); Assert.Single(snapshot.Attention); Assert.Single(snapshot.Runs);
            Assert.Null(snapshot.Attention[0].Repository);
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ReadAsync(from, from.AddHours(1), cancelled.Token));
        }
        finally { Directory.Delete(directory, true); }
    }
    [Fact]
    public async Task Service_reads_copilot_session_logs_and_not_the_files_beside_them()
    {
        var directory = Path.Combine(Path.GetTempPath(), "spindle-copilot-" + Guid.NewGuid());
        var token = TestContext.Current.CancellationToken;
        try
        {
            var session = Path.Combine(directory, ".copilot", "session-state", "s1");
            Directory.CreateDirectory(Path.Combine(session, "files"));
            var records = new object[] {
                new { type = "session.start", timestamp = "2026-10-10T09:00:00.000Z", data = new { sessionId = "s1", context = new { cwd = directory, branch = "main" } } },
                new { type = "user.message", timestamp = "2026-10-10T09:00:00.000Z", data = new { content = "Fix it" } } };
            await File.WriteAllTextAsync(Path.Combine(session, "events.jsonl"), string.Join('\n', records.Select(r => JsonSerializer.Serialize(r))) + "\n", token);
            // A copy of a file the agent worked on, which only looks like a log.
            await File.WriteAllTextAsync(Path.Combine(session, "files", "events.jsonl"),
                JsonSerializer.Serialize(new { type = "user.message", timestamp = "2026-10-10T09:30:00.000Z", data = new { content = "Not a prompt" } }) + "\n", token);
            var service = new AgentActivityService(directory);
            Assert.True(service.HasLogs);
            var from = DateTimeOffset.Parse("2026-10-10T09:00:00Z");
            var snapshot = await service.ReadAsync(from, from.AddHours(1), token);
            Assert.Equal("copilot:s1", Assert.Single(snapshot.Sessions).Id);
            Assert.Equal(AgentSource.Copilot, Assert.Single(snapshot.Runs).Source);
            Assert.Equal(from.ToUnixTimeMilliseconds(), Assert.Single(snapshot.Attention).Start);
            var uploaded = await service.ReadSessionsAsync(from, cancellationToken: token);
            Assert.Equal("copilot", AgentSessionUpload.Create(uploaded.Single().Session, null, false)!.Source);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Incremental_reads_retry_partial_records_reset_truncated_files_and_forget_on_opt_out()
    {
        var home = Path.Combine(Path.GetTempPath(), "spindle-incremental-" + Guid.NewGuid());
        var token = TestContext.Current.CancellationToken;
        try
        {
            var logs = Path.Combine(home, ".codex", "sessions"); Directory.CreateDirectory(logs);
            var path = Path.Combine(logs, "rollout-live.jsonl");
            var meta = JsonSerializer.Serialize(new { type = "session_meta", payload = new { id = "a", cwd = home } }) + "\n";
            string Touch(string time) => JsonSerializer.Serialize(new { type = "event_msg", timestamp = time, payload = new { type = "task_started" } }) + "\n";
            var first = Touch("2026-10-10T09:00:00Z"); var second = Touch("2026-10-10T09:10:00Z");
            await File.WriteAllTextAsync(path, meta + first + second[..20], token);
            var service = new AgentActivityService(home);
            var from = DateTimeOffset.Parse("2026-10-10T09:00:00Z");
            var snapshot = await service.ReadAsync(from, from.AddHours(1), token);
            Assert.Single(snapshot.Sessions.Single().Events);
            await File.AppendAllTextAsync(path, second[20..], token);
            snapshot = await service.ReadAsync(from, from.AddHours(1).AddMinutes(1), token);
            Assert.Equal(2, snapshot.Sessions.Single().Events.Count);
            Assert.Equal(25 * 60 * 1000, snapshot.Attention.Sum(a => a.Duration));
            await File.WriteAllTextAsync(Path.Combine(home, ".codex", "session_index.jsonl"),
                "{\"id\":\"a\",\"thread_name\":\"Named session\"}\n", token);
            snapshot = await service.ReadAsync(from, from.AddHours(1), token);
            Assert.Equal("Named session", snapshot.Sessions.Single().Title);
            await File.WriteAllTextAsync(path, meta + first, token);
            snapshot = await service.ReadAsync(from, from.AddHours(1), token);
            Assert.Single(snapshot.Sessions.Single().Events);
            service.Clear(); File.Delete(path);
            snapshot = await service.ReadAsync(from, from.AddHours(1), token);
            Assert.Empty(snapshot.Sessions);
        }
        finally { Directory.Delete(home, true); }
    }

}
