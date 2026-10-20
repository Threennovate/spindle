using Spindle.Domain.Common;

namespace Spindle.Domain.Workspaces;

public enum WorkspaceRole
{
    Admin,
    Member,
}

public sealed class WorkspaceMember : Entity
{
    private WorkspaceMember()
    {
    }

    public Guid WorkspaceId { get; private set; }

    public Guid UserId { get; private set; }

    public WorkspaceRole Role { get; private set; }

    public static WorkspaceMember Create(Guid workspaceId, Guid userId, WorkspaceRole role) => new()
    {
        WorkspaceId = Guard.Id(workspaceId),
        UserId = Guard.Id(userId),
        Role = role,
    };

    public void ChangeRole(WorkspaceRole role) => Role = role;
}
