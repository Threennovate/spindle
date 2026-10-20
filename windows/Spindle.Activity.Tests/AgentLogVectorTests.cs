using System.Text;
using System.Text.Json;

using Spindle.Activity;

namespace Spindle.Activity.Tests;

/// <summary>
/// The cases the Mac's Kit runs too (<c>tests/vectors/agent-logs.json</c>), so both read each agent's logs the same way.
/// </summary>
public sealed class AgentLogVectorTests
{
    private static readonly JsonElement Vectors = Load();

    public static TheoryData<string> Cases() => new(Vectors.GetProperty("cases").EnumerateArray().Select(c => c.GetProperty("name").GetString()!));

    [Theory]
    [MemberData(nameof(Cases))]
    public void Reads_what_the_Mac_reads(string name)
    {
        var vector = Vectors.GetProperty("cases").EnumerateArray().Single(c => c.GetProperty("name").GetString() == name);
        var source = AgentLogFormat.All.Single(f => f.Id == vector.GetProperty("source").GetString()).Source;
        var parser = new AgentLogParser(source);
        foreach (var line in vector.GetProperty("lines").EnumerateArray()) parser.Consume(Encoding.UTF8.GetBytes(line.GetString()!));
        var sessions = parser.Sessions.OrderBy(s => s.Id, StringComparer.Ordinal).ToArray();
        var expected = vector.GetProperty("sessions").EnumerateArray().ToArray();
        Assert.Equal(expected.Select(e => e.GetProperty("id").GetString()), sessions.Select(s => s.Id));
        foreach (var (session, want) in sessions.Zip(expected))
        {
            Assert.Equal(source, session.Source);
            Assert.Equal(want.GetProperty("parent").GetString(), session.ParentId);
            Assert.Equal(want.GetProperty("cwd").GetString(), session.Cwd);
            Assert.Equal(want.GetProperty("title").GetString(), session.Title);
            Assert.Equal(want.GetProperty("branches").EnumerateArray().Select(b => b.GetString()), session.Branches.Order(StringComparer.Ordinal));
            Assert.Equal(want.GetProperty("touches").EnumerateArray().Select(t => DateTimeOffset.Parse(t.GetString()!).ToUnixTimeMilliseconds()),
                session.Events.Where(e => e.IsTouch).Select(e => e.At));
            Assert.Equal(want.GetProperty("events").GetInt32(), session.Events.Count);
        }
        Assert.DoesNotContain(Vectors.GetProperty("secret").GetString()!, JsonSerializer.Serialize(sessions));
    }

    public static TheoryData<string, string> Paths() =>
        new(Vectors.GetProperty("discovery").EnumerateArray().Select(p => (p.GetProperty("source").GetString()!, p.GetProperty("path").GetString()!)));

    [Theory]
    [MemberData(nameof(Paths))]
    public void Finds_the_logs_the_Mac_finds(string source, string path)
    {
        var vector = Vectors.GetProperty("discovery").EnumerateArray()
            .Single(p => p.GetProperty("source").GetString() == source && p.GetProperty("path").GetString() == path);
        var format = AgentLogFormat.All.Single(f => f.Id == source);
        Assert.Equal(vector.GetProperty("log").GetBoolean(), format.IsLog(path));
        if (vector.GetProperty("session").GetString() is { } session) Assert.Equal(session, format.Session(path));
    }

    private static JsonElement Load()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "tests", "vectors", "agent-logs.json")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory.FullName, "tests", "vectors", "agent-logs.json")));
        return document.RootElement.Clone();
    }
}
