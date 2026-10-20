using Spindle.Domain.TimeEntries;

namespace Spindle.Tests.Domain;

public sealed class DescriptionRankingTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("Fix login", "fix", true)]
    [InlineData("Review the fix", "fix", true)]
    [InlineData("#142 fix", "fix", true)]
    [InlineData("Prefix it", "fix", false)]
    [InlineData("prefix, fixture", "fix", true)]
    public void A_term_starts_the_text_or_one_of_its_words(string text, string term, bool expected) =>
        Assert.Equal(expected, DescriptionRanking.StartsWord(text, term));

    [Fact]
    public void Often_and_lately_beats_once_long_ago()
    {
        (string Text, DateTimeOffset At)[] uses =
        [
            ("Standup", Now.AddDays(-1)),
            ("Standup", Now.AddDays(-2)),
            ("Old habit", Now.AddDays(-90)),
            ("Old habit", Now.AddDays(-91)),
            ("Old habit", Now.AddDays(-92)),
            ("Yesterday's one-off", Now.AddHours(-20)),
        ];

        var ranked = DescriptionRanking.Rank(uses, u => u.Text, u => u.Text, u => u.At, null, Now);

        Assert.Equal(["Standup", "Yesterday's one-off", "Old habit"], ranked.Select(u => u.Text));
    }
}
