using System.Text.Json;
using Spindle.Activity;

namespace Spindle.Activity.Tests;

/// <summary>
/// Repositories the person ignores, as the Mac's (SpindleKitTests/IgnoredRepositoriesTests.swift): hashed as the server
/// hashes them, and left out of everything but the place of their prompts.
/// </summary>
public sealed class IgnoredRepositoriesTests
{
    private const string Account = "0c000000-0000-4000-8000-000000000001";

    public static TheoryData<string, string, string> Cases()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "tests", "vectors", "ignored-repositories.json"))) directory = directory.Parent;
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory!.FullName, "tests", "vectors", "ignored-repositories.json")));
        return new(document.RootElement.GetProperty("cases").EnumerateArray()
            .Select(c => (c.GetProperty("account").GetString()!, c.GetProperty("identity").GetString()!, c.GetProperty("hash").GetString()!)).ToList());
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Hashes_a_repository_as_the_server_does(string account, string identity, string hash)
    {
        Assert.Equal(hash, IgnoredRepositories.Hash(identity, account));
        Assert.True(new IgnoredRepositories(account, [hash]).Contains(identity));
    }

    private const long Start = 1_790_000_000_000;
    private static readonly AgentRepository Atlas = new("origin:github.com/acme/atlas", "atlas");
    private static readonly AgentRepository Zeta = new("origin:github.com/acme/zeta", "zeta");
    private static readonly IgnoredRepositories IgnoringZeta = new(Account, [IgnoredRepositories.Hash(Zeta.Identity, Account)]);

    private static LocalAgentSession Session(string id, (double Minutes, bool Touch)[] events, string? parent = null) =>
        new(id, AgentSource.Claude, parent, @"C:\code\zeta", "Private work", ["secret"], events.Select(e => new AgentEvent(Start + (long)(e.Minutes * 60_000), e.Touch)).ToArray());

    [Fact]
    public void An_ignored_session_goes_as_its_prompts_times_alone_and_a_subagents_not_at_all()
    {
        var upload = AgentSessionUpload.Create(Session("claude:z", [(0, true), (5, false), (20, true)]), Zeta, titles: true, ignored: true)!;

        Assert.Equal([Start, Start + 1_200_000], upload.Touches);
        Assert.Empty(upload.Runs);
        Assert.Empty(upload.Branches);
        Assert.Null(upload.Repository);
        Assert.Null(upload.Title);
        Assert.Null(upload.ParentSessionId);
        var json = JsonSerializer.Serialize(upload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.DoesNotContain("zeta", json);
        Assert.DoesNotContain("secret", json);
        Assert.Null(AgentSessionUpload.Create(Session("claude:zz", [(6, false)], parent: "claude:z"), Zeta, titles: true, ignored: true));
    }

    [Fact]
    public async Task Ignoring_and_unignoring_sends_the_repositorys_sessions_again()
    {
        (LocalAgentSession, AgentRepository?)[] logs =
        [
            (Session("claude:a", [(0, true)]) with { Cwd = @"C:\code\atlas" }, Atlas),
            (Session("claude:z", [(10, true), (11, false)]), Zeta),
            (Session("claude:zz", [(12, false)], parent: "claude:z"), Zeta),
        ];
        var sent = new List<string[]>();
        var looks = new List<DateTimeOffset>();
        var uploader = new AgentActivityUploader(null);
        var now = DateTimeOffset.FromUnixTimeMilliseconds(Start).AddDays(1);
        Task Upload(IgnoredRepositories ignoring, int minutes) => uploader.UploadAsync(DateTimeOffset.FromUnixTimeSeconds(1_780_000_000), false,
            now.AddMinutes(minutes), (from, changed, _) =>
            {
                looks.Add(changed ?? DateTimeOffset.MinValue);
                return Task.FromResult<IReadOnlyList<(LocalAgentSession, AgentRepository?)>>(logs);
            }, (batch, _) =>
            {
                sent.Add(batch.Select(s => $"{s.SessionId} {s.Repository?.Name ?? "-"}").ToArray());
                return Task.CompletedTask;
            }, TestContext.Current.CancellationToken, ignoring: ignoring);

        await Upload(IgnoredRepositories.None, 0);
        await Upload(IgnoringZeta, 5);
        await Upload(IgnoringZeta, 10);
        await Upload(IgnoredRepositories.None, 15);

        Assert.Equal(
        [
            ["claude:a atlas", "claude:z zeta", "claude:zz zeta"],
            // Its prompts' times alone; the subagent, whose runs the server deleted with the rest, says nothing now.
            ["claude:z -"],
            ["claude:z zeta", "claude:zz zeta"],
        ], sent);
        // Each change of the list looks at the whole history again.
        Assert.Equal([DateTimeOffset.MinValue, DateTimeOffset.MinValue, now.AddMinutes(4), DateTimeOffset.MinValue], looks);
    }

    [Fact]
    public async Task Reading_leaves_an_ignored_repository_out_but_the_place_of_its_prompts()
    {
        var home = Path.Combine(Path.GetTempPath(), "spindle-ignored-" + Guid.NewGuid());
        var token = TestContext.Current.CancellationToken;
        try
        {
            string Repository(string name)
            {
                var folder = Path.Combine(home, "code", name);
                Directory.CreateDirectory(Path.Combine(folder, ".git"));
                File.WriteAllText(Path.Combine(folder, ".git", "config"), $"[remote \"origin\"]\n\turl = git@github.com:acme/{name}.git\n");
                return folder;
            }
            var (atlas, zeta) = (Repository("atlas"), Repository("zeta"));
            var project = Path.Combine(home, ".claude", "projects", "work");
            Directory.CreateDirectory(project);
            string Record(string session, string cwd, string time) => JsonSerializer.Serialize(new
            {
                type = "user", sessionId = session, cwd, timestamp = time, message = new { content = "Do the thing" },
            }) + "\n";
            await File.WriteAllTextAsync(Path.Combine(project, "a.jsonl"),
                Record("a", atlas, "2026-10-10T09:00:00Z") + Record("a", atlas, "2026-10-10T09:30:00Z"), token);
            await File.WriteAllTextAsync(Path.Combine(project, "z.jsonl"), Record("z", zeta, "2026-10-10T09:10:00Z"), token);
            var service = new AgentActivityService(home) { Ignoring = IgnoringZeta };

            var snapshot = await service.ReadAsync(DateTimeOffset.Parse("2026-10-10T00:00:00Z"), DateTimeOffset.Parse("2026-10-11T00:00:00Z"), token);

            // Atlas's attention ends at zeta's prompt; zeta's is kept apart without a name, and nothing else of it shows.
            Assert.Equal(["atlas 10", "atlas 15"], snapshot.Attention.Select(a => $"{a.Repository?.Name} {a.Duration / 60_000}"));
            var ignored = Assert.Single(snapshot.Ignored);
            Assert.Equal((null, 15L), (ignored.Repository, ignored.Duration / 60_000));
            Assert.Equal(["atlas"], snapshot.Runs.Select(r => r.Repository?.Name).Distinct());
            Assert.Equal(["claude:a"], snapshot.Sessions.Select(s => s.Id));
            Assert.Equal(3, snapshot.Evidence.Count);

            // The logs stay as they were: off the list, it comes back.
            service.Ignoring = IgnoredRepositories.None;
            var again = await service.ReadAsync(DateTimeOffset.Parse("2026-10-10T00:00:00Z"), DateTimeOffset.Parse("2026-10-11T00:00:00Z"), token);
            Assert.Equal(["atlas 10", "zeta 15", "atlas 15"], again.Attention.Select(a => $"{a.Repository?.Name} {a.Duration / 60_000}"));
            Assert.Empty(again.Ignored);
        }
        finally { Directory.Delete(home, true); }
    }
}
