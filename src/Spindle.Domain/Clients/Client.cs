using Spindle.Domain.Common;

namespace Spindle.Domain.Clients;

/// <summary>A customer that projects are done for.</summary>
public sealed class Client : Entity
{
    public const int NameMaxLength = 100;

    private Client()
    {
        Name = string.Empty;
    }

    public Guid WorkspaceId { get; private set; }

    public string Name { get; private set; }

    /// <summary>Archived clients keep their history but are hidden when picking a client.</summary>
    public DateTimeOffset? ArchivedAt { get; private set; }

    public static Client Create(Guid workspaceId, string name) => new()
    {
        WorkspaceId = Guard.Id(workspaceId),
        Name = Guard.Name(name, NameMaxLength),
    };

    public void Rename(string name) => Name = Guard.Name(name, NameMaxLength);

    public void Archive(DateTimeOffset now) => ArchivedAt ??= now;

    public void Restore() => ArchivedAt = null;
}
