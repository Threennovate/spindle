using System.Globalization;
using System.Text.Json;

using Spindle.Activity;

namespace Spindle.Activity.Tests;

/// <summary>
/// The cases the Mac's Kit runs too (<c>tests/vectors/untracked-activity.json</c>), so both find the same time nobody
/// tracked, offer the same earlier start, and name the same work in the reminder.
/// </summary>
public sealed class UntrackedActivityVectorTests
{
    private static readonly JsonElement Vectors = Load();
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById(Vectors.GetProperty("timeZone").GetString()!);

    private static readonly Dictionary<string, LinkableProject> Projects = Vectors.GetProperty("projects").EnumerateObject()
        .ToDictionary(p => p.Name, p => new LinkableProject(p.Value.GetProperty("id").GetGuid(), p.Value.GetProperty("name").GetString()!));

    private static readonly Dictionary<string, AgentRepository> Repositories = Vectors.GetProperty("repositories").EnumerateObject()
        .ToDictionary(r => r.Name, r => new AgentRepository(r.Value.GetProperty("identity").GetString()!, r.Value.GetProperty("name").GetString()!));

    private static readonly RepositoryLinks Links = new(
        Vectors.GetProperty("chosen").EnumerateObject().ToDictionary(l => Repositories[l.Name].Identity, l => Projects[l.Value.GetString()!].Id),
        new Dictionary<string, Guid>());

    private static Guid? Project(AgentRepository repository) => Links.ProjectFor(repository, Projects.Values.ToList());

    public static TheoryData<string> StretchCases() => Names("stretches");
    public static TheoryData<string> EarlierStartCases() => Names("earlierStart");
    public static TheoryData<string> OngoingCases() => Names("ongoing");

    [Theory]
    [MemberData(nameof(StretchCases))]
    public void Finds_the_stretches_the_Mac_finds(string name)
    {
        var vector = Case("stretches", name);
        var day = DateOnly.ParseExact(vector.GetProperty("day").GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var from = Local(day.ToDateTime(TimeOnly.MinValue));
        var to = Local(day.AddDays(1).ToDateTime(TimeOnly.MinValue));
        var found = UntrackedActivity.Stretches(Attention(vector), Covered(vector), new TimeRange(from, to), At(vector.GetProperty("now")), Project);
        Assert.Equal(vector.GetProperty("expected").EnumerateArray().Select(Expected), found.Select(Text));
    }

    [Theory]
    [MemberData(nameof(EarlierStartCases))]
    public void Offers_the_start_the_Mac_offers(string name)
    {
        var vector = Case("earlierStart", name);
        var found = UntrackedActivity.EarlierStart(At(vector.GetProperty("start")), Attention(vector), Covered(vector), At(vector.GetProperty("notBefore")), Project);
        Assert.Equal(ExpectedOrNull(vector), found is null ? null : Text(found));
    }

    [Theory]
    [MemberData(nameof(OngoingCases))]
    public void Names_the_work_the_Mac_names(string name)
    {
        var vector = Case("ongoing", name);
        var found = UntrackedActivity.Ongoing(Attention(vector), Covered(vector), At(vector.GetProperty("notBefore")), At(vector.GetProperty("now")), Project);
        Assert.Equal(ExpectedOrNull(vector), found is null ? null : Text(found));
    }

    private static string? ExpectedOrNull(JsonElement vector) =>
        vector.GetProperty("expected") is { ValueKind: not JsonValueKind.Null } expected ? Expected(expected) : null;

    private static string Expected(JsonElement stretch) =>
        $"{stretch.GetProperty("start").GetString()}–{stretch.GetProperty("end").GetString()} [{string.Join(",", stretch.GetProperty("projects").EnumerateArray().Select(p => p.GetString()))}] unlinked {stretch.GetProperty("unlinked").GetInt32()}";

    private static string Text(UntrackedStretch stretch) =>
        $"{Clock(stretch.Range.Start)}–{Clock(stretch.Range.End)} [{string.Join(",", stretch.Projects.Select(id => Projects.Single(p => p.Value.Id == id).Key))}] unlinked {stretch.Unlinked / 60_000}";

    private static string Clock(long at) => TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeMilliseconds(at), Zone).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    private static List<AttentionSegment> Attention(JsonElement vector)
    {
        var touches = vector.GetProperty("sessions").EnumerateArray().SelectMany(s =>
        {
            var repository = s.TryGetProperty("repository", out var key) && key.ValueKind != JsonValueKind.Null ? Repositories[key.GetString()!] : null;
            return s.GetProperty("touches").EnumerateArray().Select(t => new AgentTouch(At(t), s.GetProperty("id").GetString()!, repository));
        }).ToList();
        var first = touches.Min(t => t.At);
        return AttentionTimeline.Build(touches, first - 86_400_000, first + 2 * 86_400_000);
    }

    private static List<TimeRange> Covered(JsonElement vector) =>
        vector.GetProperty("covered").EnumerateArray().Select(c => new TimeRange(At(c[0]), At(c[1]))).ToList();

    private static long At(JsonElement value) =>
        Local(DateTime.ParseExact(value.GetString()!, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));

    private static long Local(DateTime time) => new DateTimeOffset(time, Zone.GetUtcOffset(time)).ToUnixTimeMilliseconds();

    private static TheoryData<string> Names(string kind) => new(Vectors.GetProperty(kind).EnumerateArray().Select(c => c.GetProperty("name").GetString()!));

    private static JsonElement Case(string kind, string name) => Vectors.GetProperty(kind).EnumerateArray().Single(c => c.GetProperty("name").GetString() == name);

    private static JsonElement Load()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "tests", "vectors", "untracked-activity.json")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory.FullName, "tests", "vectors", "untracked-activity.json")));
        return document.RootElement.Clone();
    }
}
