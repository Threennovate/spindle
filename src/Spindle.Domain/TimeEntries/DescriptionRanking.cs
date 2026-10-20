namespace Spindle.Domain.TimeEntries;

/// <summary>
/// Orders what the person typed before, to offer it again while they type: text that starts with what is typed (or
/// has a word that does) comes before text that only contains it, then text from the preferred project, then text used
/// often and lately before text used once long ago.
/// </summary>
public static class DescriptionRanking
{
    /// <summary>A use counts half as much after this long, so last week's habit beats last year's.</summary>
    private static readonly TimeSpan HalfLife = TimeSpan.FromDays(14);

    /// <summary>
    /// The distinct items among <paramref name="uses"/>, best first for <paramref name="term"/> (which they are
    /// expected to match already). Each item is represented by its most recent use; the uses may come in any order.
    /// </summary>
    public static List<T> Rank<T, TKey>(
        IEnumerable<T> uses,
        Func<T, TKey> key,
        Func<T, string> text,
        Func<T, DateTimeOffset> at,
        string? term,
        DateTimeOffset now,
        Func<T, bool>? preferred = null)
        where TKey : notnull
    {
        term = string.IsNullOrWhiteSpace(term) ? null : term.Trim();
        return uses
            .GroupBy(key)
            .Select(group =>
            {
                var latest = group.MaxBy(at)!;
                return new
                {
                    Item = latest,
                    Starts = term is not null && StartsWord(text(latest), term),
                    Preferred = preferred is not null && group.Any(preferred),
                    Weight = group.Sum(use => Math.Pow(0.5, Math.Max(0, (now - at(use)).TotalDays) / HalfLife.TotalDays)),
                    Latest = at(latest),
                };
            })
            .OrderByDescending(r => r.Starts)
            .ThenByDescending(r => r.Preferred)
            .ThenByDescending(r => r.Weight)
            .ThenByDescending(r => r.Latest)
            .Select(r => r.Item)
            .ToList();
    }

    /// <summary>Whether the text, or one of its words, starts with the term.</summary>
    public static bool StartsWord(string text, string term)
    {
        for (var i = text.IndexOf(term, StringComparison.OrdinalIgnoreCase); i >= 0; i = text.IndexOf(term, i + 1, StringComparison.OrdinalIgnoreCase))
        {
            if (i == 0 || !char.IsLetterOrDigit(text[i - 1]))
            {
                return true;
            }
            if (i + 1 >= text.Length)
            {
                break;
            }
        }
        return false;
    }
}
