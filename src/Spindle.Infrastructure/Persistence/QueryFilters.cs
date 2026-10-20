namespace Spindle.Infrastructure.Persistence;

public static class QueryFilters
{
    /// <summary>Hides tombstones. Sync queries ignore it so clients learn about deletions.</summary>
    public const string SoftDelete = nameof(SoftDelete);
}
