using System.Globalization;
using System.Text.Json;
using Spindle.Windows.Model;

namespace Spindle.Windows.Tests;

/// <summary>Every case the Mac and the web run, from the repository's shared vectors, and the Mac's mention cases.</summary>
public sealed class EntrySentenceTests
{
    private static readonly string VectorsPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../tests/vectors/entry-sentence.json"));

    public static TheoryData<int> Cases()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(VectorsPath));
        var data = new TheoryData<int>();
        for (var index = 0; index < document.RootElement.GetProperty("cases").GetArrayLength(); index++) data.Add(index);
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void ReadsTheSharedVectors(int index)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(VectorsPath));
        var root = document.RootElement;
        var zone = TimeZoneInfo.FindSystemTimeZoneById(root.GetProperty("timeZone").GetString()!);
        var nowLocal = DateTime.Parse(root.GetProperty("now").GetString()!, CultureInfo.InvariantCulture);
        var now = new DateTimeOffset(nowLocal, zone.GetUtcOffset(nowLocal));
        var vector = root.GetProperty("cases")[index];
        var text = vector.GetProperty("text").GetString()!;
        var options = new EntrySentence.Options();
        if (vector.TryGetProperty("options", out var given))
        {
            if (given.TryGetProperty("hour12", out var hour12)) options = options with { Hour12 = hour12.GetBoolean() };
            if (given.TryGetProperty("dateFormat", out var format)) options = options with { DateFormat = format.GetString()! };
        }
        var chips = new List<EntrySentence.Chip>();
        if (vector.TryGetProperty("chips", out var chipList))
        {
            foreach (var chip in chipList.EnumerateArray())
            {
                chips.Add(chip.GetProperty("kind").GetString() switch
                {
                    "project" => new EntrySentence.Chip.Project(chip.GetProperty("projectId").GetString()!, chip.TryGetProperty("taskId", out var task) ? task.GetString() : null),
                    "client" => new EntrySentence.Chip.Client(chip.GetProperty("clientId").GetString()!),
                    "tag" => new EntrySentence.Chip.Tag(chip.GetProperty("tagId").GetString()!),
                    _ => new EntrySentence.Chip.Billable(),
                });
            }
        }
        var ends = new Dictionary<DateOnly, DateTimeOffset[]>();
        if (vector.TryGetProperty("dayEnds", out var dayEnds))
        {
            foreach (var day in dayEnds.EnumerateObject())
                ends[DateOnly.Parse(day.Name, CultureInfo.InvariantCulture)] = day.Value.EnumerateArray().Select(v => Local(v.GetString()!, zone)).ToArray();
        }
        var reading = EntrySentence.Read(text, chips, options);
        var time = new UserTime(zone, 1, options.Hour12);
        var span = EntrySentence.Resolve(reading.Time, now, time, day => ends.GetValueOrDefault(day) ?? []);
        var label = $"“{text}”";
        Assert.True(vector.GetProperty("description").GetString() == reading.Description, $"{label}: description “{reading.Description}”");
        Assert.True(vector.GetProperty("timeText").GetString() == reading.TimeText, $"{label}: time text “{reading.TimeText}”");
        if (vector.GetProperty("start").ValueKind == JsonValueKind.Null) Assert.True(reading.Time is null, $"{label}: read a time");
        else Assert.True(Local(vector.GetProperty("start").GetString()!, zone) == span.Start, $"{label}: start {TimeZoneInfo.ConvertTime(span.Start, zone):yyyy-MM-ddTHH:mm}");
        if (vector.GetProperty("stop").ValueKind == JsonValueKind.Null) Assert.True(span.Stop is null, $"{label}: has a stop");
        else Assert.True(Local(vector.GetProperty("stop").GetString()!, zone) == span.Stop, $"{label}: stop {(span.Stop is { } stop ? TimeZoneInfo.ConvertTime(stop, zone).ToString("yyyy-MM-ddTHH:mm") : "none")}");
        if (vector.TryGetProperty("projectId", out var project)) Assert.Equal(project.GetString(), reading.ProjectId);
        if (vector.TryGetProperty("taskId", out var taskId)) Assert.Equal(taskId.GetString(), reading.TaskId);
        if (vector.TryGetProperty("clientId", out var client)) Assert.Equal(client.GetString(), reading.ClientId);
        if (vector.TryGetProperty("tagIds", out var tags)) Assert.Equal(tags.EnumerateArray().Select(t => t.GetString()!).ToArray(), reading.TagIds);
        Assert.Equal(vector.TryGetProperty("billable", out var billable) && billable.GetBoolean(), reading.Billable);
    }

    private static DateTimeOffset Local(string text, TimeZoneInfo zone)
    {
        var local = DateTime.Parse(text, CultureInfo.InvariantCulture);
        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }

    [Theory]
    [InlineData("Review @ac", 10, '@', "ac", 7, 10)]
    [InlineData("Review #rev now", 11, '#', "rev", 7, 11)]
    [InlineData("Review @", 8, '@', "", 7, 8)]
    public void MentionsAreTheWordAtTheCaret(string text, int caret, char sign, string query, int start, int end) =>
        Assert.Equal(new EntrySentence.Mention(sign, query, start, end), EntrySentence.MentionAt(text, caret));

    [Theory]
    [InlineData("mail ana@x.com", 14)]
    [InlineData("Fix #482 later", 14)]
    public void OtherWordsAreNotMentions(string text, int caret) => Assert.Null(EntrySentence.MentionAt(text, caret));

    [Theory]
    [InlineData("Review @ac", 7, 10, "Review ", 7)]
    [InlineData("Review @ac since 9", 7, 10, "Review since 9", 7)]
    [InlineData("@ac Review", 0, 3, "Review", 0)]
    public void PickingAMentionRemovesItsWord(string text, int start, int end, string expected, int caret) =>
        Assert.Equal((expected, caret), EntrySentence.Removing(new EntrySentence.Mention('@', "ac", start, end), text));
}
