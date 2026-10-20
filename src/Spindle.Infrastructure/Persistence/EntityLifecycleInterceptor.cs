using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Spindle.Domain.Common;

namespace Spindle.Infrastructure.Persistence;

/// <summary>
/// Stamps <see cref="Entity.CreatedAt"/> and <see cref="Entity.UpdatedAt"/>, and turns deletes into tombstones.
/// </summary>
internal sealed class EntityLifecycleInterceptor(TimeProvider timeProvider) : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Apply(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Apply(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void Apply(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        var now = PostgresTimestamp.Normalize(timeProvider.GetUtcNow());

        foreach (var entry in context.ChangeTracker.Entries<Entity>())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    entry.Property(e => e.CreatedAt).CurrentValue = now;
                    entry.Property(e => e.UpdatedAt).CurrentValue = now;
                    break;

                case EntityState.Modified:
                    entry.Property(e => e.CreatedAt).IsModified = false;
                    entry.Property(e => e.UpdatedAt).CurrentValue = now;
                    break;

                case EntityState.Deleted:
                    entry.State = EntityState.Modified;
                    entry.Property(e => e.CreatedAt).IsModified = false;
                    entry.Property(e => e.UpdatedAt).CurrentValue = now;
                    entry.Property(e => e.DeletedAt).CurrentValue = now;
                    break;
            }
        }

        // Match values returned immediately after a save to the database's UTC/microsecond representation.
        foreach (var entry in context.ChangeTracker.Entries()
            .Where(e => e.State is EntityState.Added or EntityState.Modified))
        {
            foreach (var property in entry.Properties)
            {
                if (property.CurrentValue is DateTimeOffset value)
                {
                    property.CurrentValue = PostgresTimestamp.Normalize(value);
                }
            }
        }
    }
}
