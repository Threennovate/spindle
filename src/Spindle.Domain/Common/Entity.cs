namespace Spindle.Domain.Common;

/// <summary>
/// Base for every entity that is synchronised to clients.
/// </summary>
/// <remarks>
/// IDs can be generated on the client so records can be created offline.
/// Soft-deleted rows carry a <see cref="DeletedAt"/> tombstone. <see cref="Version"/> maps
/// to PostgreSQL <c>xmin</c> for optimistic concurrency; it is not a global sync cursor.
/// Timestamps and tombstones are maintained by the persistence layer.
/// </remarks>
public abstract class Entity
{
    public Guid Id { get; protected set; } = Guid.CreateVersion7();

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public DateTimeOffset? DeletedAt { get; private set; }

    public uint Version { get; private set; }
}
