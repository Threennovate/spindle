using System.Text.Json;
using NodaTime;
using NodaTime.Text;
using Spindle.Api.AgentActivity;
using Spindle.Domain.AgentActivity;

namespace Spindle.Tests.Domain;

/// <summary>
/// The cases the Mac's Kit runs too (<c>tests/vectors/split-suggestions.json</c>), so the server suggests the split the
/// Mac does. The Mac works day by day; the server builds attention over a longer range and looks at the entry's days.
/// </summary>
public sealed class SplitSuggestionVectorTests
{
    private static readonly JsonElement Vectors = Load();
    private static readonly DateTimeZone Zone = DateTimeZoneProviders.Tzdb[Vectors.GetProperty("timeZone").GetString()!];
    private static readonly LocalDateTimePattern Pattern = LocalDateTimePattern.CreateWithInvariantCulture("uuuu-MM-dd HH:mm");

    private static readonly Dictionary<string, LinkableProject> Projects = Vectors.GetProperty("projects").EnumerateObject()
        .ToDictionary(p => p.Name, p => new LinkableProject(p.Value.GetProperty("id").GetGuid(), p.Value.GetProperty("name").GetString()!));

    private static readonly Dictionary<string, AgentRepository> Repositories = Vectors.GetProperty("repositories").EnumerateObject()
        .ToDictionary(r => r.Name, r => new AgentRepository(r.Value.GetProperty("identity").GetString()!, r.Value.GetProperty("name").GetString()!));

    private static readonly Dictionary<string, Guid> Tasks = Vectors.GetProperty("tasks").EnumerateObject()
        .ToDictionary(t => t.Name, t => t.Value.GetProperty("id").GetGuid());

    public static TheoryData<string> Suggestions() => new(Vectors.GetProperty("suggestions").EnumerateArray().Select(c => c.GetProperty("name").GetString()!));

    public static TheoryData<string> Learning() => new(Vectors.GetProperty("learning").EnumerateArray().Select(c => c.GetProperty("name").GetString()!));

    [Theory]
    [MemberData(nameof(Suggestions))]
    public void Suggests_what_the_Mac_suggests(string name)
    {
        var vector = Case("suggestions", name);
        var entry = vector.GetProperty("entry");
        var start = Local(entry.GetProperty("start"));
        var stop = Local(entry.GetProperty("stop"));
        // Attention over a few days around the entry, as the server builds it for a range of entries.
        var attention = AttentionTimeline.Build(Touches(vector), start.AddDays(-2).ToUnixTimeMilliseconds(), stop.AddDays(2).ToUnixTimeMilliseconds());
        var links = new RepositoryLinks(LinksOf(vector, "chosen"), LinksOf(vector, "learned"), RulesOf(vector), NoProjectOf(vector));

        var suggestion = SplitSuggestions.Suggest(
            start, stop, Projects[entry.GetProperty("project").GetString()!].Id, attention, links, Projects.Values.ToList(), Zone);

        var expected = vector.GetProperty("expected");
        if (expected.ValueKind == JsonValueKind.Null)
        {
            Assert.Null(suggestion);
            return;
        }

        Assert.NotNull(suggestion);
        Assert.Equal(
            expected.GetProperty("parts").EnumerateArray().Select(p =>
                $"{Target(p)} {p.GetProperty("percent").GetInt32()}% {p.GetProperty("attention").GetInt64()}s+{p.GetProperty("unattributed").GetInt64()}s"),
            suggestion.Parts.Select(p => $"{Name(p.Target)} {p.Percent}% {p.Attention / 1000}s+{p.Unattributed / 1000}s"));
        Assert.Equal(expected.GetProperty("coverage").GetDouble(), suggestion.Coverage, 9);
        Assert.Equal(
            expected.GetProperty("parts").EnumerateArray().Select(p => p.TryGetProperty("task", out var task) ? task.GetString() : null),
            suggestion.Parts.Select(p => p.TaskId is { } id ? Tasks.Single(t => t.Value == id).Key : null));
        Assert.Equal(
            expected.GetProperty("parts").EnumerateArray().Select(p => $"{Target(p)} {p.GetProperty("prompts").GetInt32()}"),
            suggestion.Parts.Select(p => $"{Name(p.Target)} {suggestion.Touches.GetValueOrDefault(p.Target)}"));
        Assert.Equal(expected.GetProperty("strength").GetString(), suggestion.Strength.ToString().ToLowerInvariant());

        var stretches = expected.GetProperty("stretches");
        if (stretches.ValueKind == JsonValueKind.Null)
        {
            Assert.Null(suggestion.Stretches);
        }
        else
        {
            Assert.NotNull(suggestion.Stretches);
            Assert.Equal(
                stretches.EnumerateArray().Select(s => $"{Target(s)} {Local(s.GetProperty("start")):O}–{Local(s.GetProperty("end")):O}"),
                suggestion.Stretches.Select(s =>
                    $"{Name(s.Target)} {DateTimeOffset.FromUnixTimeMilliseconds(s.Start).ToOffset(start.Offset):O}–{DateTimeOffset.FromUnixTimeMilliseconds(s.End).ToOffset(start.Offset):O}"));
        }
    }

    public static TheoryData<string, string, bool> BranchPatterns() => new(Vectors.GetProperty("branchPatterns").EnumerateArray()
        .Select(c => (c.GetProperty("pattern").GetString()!, c.GetProperty("branch").GetString()!, c.GetProperty("matches").GetBoolean())));

    [Theory]
    [MemberData(nameof(BranchPatterns))]
    public void Reads_branch_patterns_as_the_Mac_does(string pattern, string branch, bool matches) =>
        Assert.Equal(matches, BranchRule.Match(pattern, branch));

    [Theory]
    [MemberData(nameof(Learning))]
    public void Learns_the_links_the_Mac_learns(string name)
    {
        var vector = Case("learning", name);
        var entries = vector.GetProperty("entries").EnumerateArray().Select(e => new RepositoryLinks.Entry(
            Local(e.GetProperty("start")).ToUnixTimeMilliseconds(),
            Local(e.GetProperty("stop")).ToUnixTimeMilliseconds(),
            Projects[e.GetProperty("project").GetString()!].Id,
            e.TryGetProperty("split", out var split) && split.GetBoolean())).ToList();
        var from = entries.Min(e => e.Start) - TimeSpan.FromDays(1).Ticks / TimeSpan.TicksPerMillisecond;
        var attention = AttentionTimeline.Build(Touches(vector), from, entries.Max(e => e.Stop!.Value) + 86_400_000);

        var learned = RepositoryLinks.Learn(entries, attention, now: entries.Max(e => e.Stop!.Value));

        Assert.Equal(LinksOf(vector, "expected").OrderBy(l => l.Key), learned.OrderBy(l => l.Key));
    }

    private static JsonElement Case(string list, string name) =>
        Vectors.GetProperty(list).EnumerateArray().Single(c => c.GetProperty("name").GetString() == name);

    /// <summary>
    /// The prompts of top-level sessions; a subagent's are not the person's. An ignored repository's come as the apps
    /// upload them: times alone, as a folder outside git's.
    /// </summary>
    private static IEnumerable<AgentTouch> Touches(JsonElement vector)
    {
        var ignored = vector.TryGetProperty("ignored", out var keys) ? keys.EnumerateArray().Select(r => r.GetString()!).ToHashSet() : [];
        return vector.GetProperty("sessions").EnumerateArray()
            .Where(s => !s.TryGetProperty("parent", out var parent) || parent.ValueKind == JsonValueKind.Null)
            .SelectMany(s =>
            {
                var key = s.GetProperty("repository").GetString();
                if (key is not null && ignored.Contains(key))
                {
                    return s.GetProperty("touches").EnumerateArray()
                        .Select(t => new AgentTouch(Local(t).ToUnixTimeMilliseconds(), s.GetProperty("id").GetString()!, null, []));
                }

                var repository = key is null ? null : Repositories[key];
                var branches = s.TryGetProperty("branches", out var list) ? list.EnumerateArray().Select(b => b.GetString()!).ToArray() : [];
                return s.GetProperty("touches").EnumerateArray()
                    .Select(t => new AgentTouch(Local(t).ToUnixTimeMilliseconds(), s.GetProperty("id").GetString()!, repository, branches));
            })
            .ToList();
    }

    private static Dictionary<string, Guid> LinksOf(JsonElement vector, string property) =>
        vector.TryGetProperty(property, out var links)
            ? links.EnumerateObject().ToDictionary(l => Repositories[l.Name].Identity, l => Projects[l.Value.GetString()!].Id)
            : [];

    private static HashSet<string> NoProjectOf(JsonElement vector) =>
        vector.TryGetProperty("noProject", out var repositories) ? repositories.EnumerateArray().Select(r => Repositories[r.GetString()!].Identity).ToHashSet() : [];

    private static Dictionary<string, IReadOnlyList<BranchRule>> RulesOf(JsonElement vector) =>
        vector.TryGetProperty("rules", out var rules)
            ? rules.EnumerateObject().ToDictionary(
                r => Repositories[r.Name].Identity,
                r => (IReadOnlyList<BranchRule>)r.Value.EnumerateArray().Select(rule => new BranchRule(
                    rule.GetProperty("pattern").GetString()!,
                    Projects[rule.GetProperty("project").GetString()!].Id,
                    rule.TryGetProperty("task", out var task) ? Tasks[task.GetString()!] : null)).ToList())
            : [];

    private static string Target(JsonElement part) =>
        part.TryGetProperty("project", out var project) ? project.GetString()! : "repository " + part.GetProperty("repository").GetString();

    private static string Name(SplitTarget target) =>
        target.ProjectId is { } id ? Projects.Single(p => p.Value.Id == id).Key : "repository " + Repositories.Single(r => r.Value == target.Repository).Key;

    private static DateTimeOffset Local(JsonElement value) =>
        Pattern.Parse(value.GetString()!).Value.InZoneStrictly(Zone).ToDateTimeOffset();

    private static JsonElement Load()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "tests", "vectors", "split-suggestions.json")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory.FullName, "tests", "vectors", "split-suggestions.json")));
        return document.RootElement.Clone();
    }
}
