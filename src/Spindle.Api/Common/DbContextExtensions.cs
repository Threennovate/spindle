using Microsoft.EntityFrameworkCore;

namespace Spindle.Api.Common;

internal static class DbContextExtensions
{
    /// <summary>
    /// Runs the operation in a transaction. On a transient SQL error the whole operation is retried, so it must
    /// load everything it changes itself; the change tracker is cleared before each attempt.
    /// </summary>
    public static Task<T> InTransactionAsync<T>(
        this DbContext db,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        return strategy.ExecuteAsync(async ct =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var result = await operation(ct);
            await transaction.CommitAsync(ct);
            return result;
        }, cancellationToken);
    }
}
