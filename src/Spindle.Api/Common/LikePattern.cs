namespace Spindle.Api.Common;

internal static class LikePattern
{
    public static string Contains(string text) =>
        "%" + text.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal) + "%";
}
