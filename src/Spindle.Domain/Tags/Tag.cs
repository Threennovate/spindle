using Spindle.Domain.Common;

namespace Spindle.Domain.Tags;

public sealed class Tag : Entity
{
    public const int NameMaxLength = 50;

    private Tag()
    {
        Name = string.Empty;
    }

    public Guid WorkspaceId { get; private set; }

    public string Name { get; private set; }

    public static Tag Create(Guid workspaceId, string name) => new()
    {
        WorkspaceId = Guard.Id(workspaceId),
        Name = Guard.Name(name, NameMaxLength),
    };

    public void Rename(string name) => Name = Guard.Name(name, NameMaxLength);
}
