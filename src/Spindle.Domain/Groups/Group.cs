using Spindle.Domain.Common;

namespace Spindle.Domain.Groups;

/// <summary>A named set of members, e.g. "Design" or "Contractors", to filter and compare in reports.</summary>
public sealed class Group : Entity
{
    public const int NameMaxLength = 100;

    private Group()
    {
        Name = string.Empty;
    }

    public Guid WorkspaceId { get; private set; }

    public string Name { get; private set; }

    public static Group Create(Guid workspaceId, string name) => new()
    {
        WorkspaceId = Guard.Id(workspaceId),
        Name = Guard.Name(name, NameMaxLength),
    };

    public void Rename(string name) => Name = Guard.Name(name, NameMaxLength);
}

public sealed class GroupMember : Entity
{
    private GroupMember()
    {
    }

    public Guid GroupId { get; private set; }

    public Guid UserId { get; private set; }

    public static GroupMember Create(Guid groupId, Guid userId) => new()
    {
        GroupId = Guard.Id(groupId),
        UserId = Guard.Id(userId),
    };
}
