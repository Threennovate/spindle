using System.Globalization;
using System.Text.Json;

using Spindle.Activity;

namespace Spindle.Activity.Tests;

/// <summary>
/// The cases the Mac's Kit runs too (<c>tests/vectors/split-drift.json</c>), so both offer to end a split at the same
/// moment.
/// </summary>
public sealed class SplitDriftVectorTests
{
    private static readonly JsonElement Vectors = Load();
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById(Vectors.GetProperty("timeZone").GetString()!);

    private static readonly Dictionary<string, LinkableProject> Projects = Vectors.GetProperty("projects").EnumerateObject()
        .ToDictionary(p => p.Name, p => new LinkableProject(p.Value.GetProperty("id").GetGuid(), p.Value.GetProperty("name").GetString()!));

    private static readonly Dictionary<string, AgentRepository> Repositories = Vectors.GetProperty("repositories").EnumerateObject()
        .ToDictionary(r => r.Name, r => new AgentRepository(r.Value.GetProperty("identity").GetString()!, r.Value.GetProperty("name").GetString()!));

    public static TheoryData<string> Cases() => new(Vectors.GetProperty("cases").EnumerateArray().Select(c => c.GetProperty("name").GetString()!));

    [Theory]
    [MemberData(nameof(Cases))]
    public void Offers_what_the_Mac_offers(string name)
    {
        var vector = Vectors.GetProperty("cases").EnumerateArray().Single(c => c.GetProperty("name").GetString() == name);
        var entry = vector.GetProperty("entry");
        var start = Local(entry.GetProperty("start"));
        var now = Local(entry.GetProperty("now"));
        var parts = entry.GetProperty("parts").EnumerateArray().Select(p => Projects[p.GetProperty("project").GetString()!].Id).ToList();
        var percents = entry.GetProperty("parts").EnumerateArray().Select(p => p.GetProperty("percent").GetInt32()).ToList();
        var chosen = vector.TryGetProperty("chosen", out var links)
            ? links.EnumerateObject().ToDictionary(l => Repositories[l.Name].Identity, l => Projects[l.Value.GetString()!].Id)
            : [];
        var repositoryLinks = new RepositoryLinks(chosen, new Dictionary<string, Guid>());
        var attention = AttentionTimeline.Build(Touches(vector), start.AddDays(-1).ToUnixTimeMilliseconds(), now.AddDays(1).ToUnixTimeMilliseconds());

        var drift = SplitDrift.Detect(parts, start.ToUnixTimeMilliseconds(), Local(entry.GetProperty("statedAt")).ToUnixTimeMilliseconds(),
            now.ToUnixTimeMilliseconds(), attention, repository => repositoryLinks.ProjectFor(repository, Projects.Values.ToList()));

        var expected = vector.GetProperty("expected");
        if (expected.ValueKind == JsonValueKind.Null)
        {
            Assert.Null(drift);
            return;
        }

        Assert.NotNull(drift);
        var at = Local(expected.GetProperty("at")).ToUnixTimeMilliseconds();
        Assert.Equal(
            expected.TryGetProperty("quiet", out var quiet)
                ? new SplitDrift(quiet.EnumerateArray().Select(q => Projects[q.GetString()!].Id).ToList(), null, at)
                : new SplitDrift([], Projects[expected.GetProperty("joined").GetString()!].Id, at),
            drift);
        var nullable = parts.Select(p => (Guid?)p).ToList();
        Assert.Equal(
            expected.GetProperty("next").EnumerateArray().Select(p => $"{p.GetProperty("project").GetString()} {p.GetProperty("percent").GetInt32()}"),
            drift.ContinuingProjects(nullable).Zip(drift.ContinuingPercents(nullable, percents)).Select(p => $"{Projects.Single(q => q.Value.Id == p.First).Key} {p.Second}"));
    }

    private static IEnumerable<AgentTouch> Touches(JsonElement vector) =>
        vector.GetProperty("sessions").EnumerateArray().SelectMany(s =>
        {
            var repository = s.TryGetProperty("repository", out var key) && key.ValueKind != JsonValueKind.Null ? Repositories[key.GetString()!] : null;
            return s.GetProperty("touches").EnumerateArray().Select(t => new AgentTouch(Local(t).ToUnixTimeMilliseconds(), s.GetProperty("id").GetString()!, repository));
        }).ToList();

    private static DateTimeOffset Local(JsonElement value)
    {
        var time = DateTime.ParseExact(value.GetString()!, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        return new DateTimeOffset(time, Zone.GetUtcOffset(time));
    }

    private static JsonElement Load()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "tests", "vectors", "split-drift.json")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory.FullName, "tests", "vectors", "split-drift.json")));
        return document.RootElement.Clone();
    }
}
