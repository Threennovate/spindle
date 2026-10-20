using Spindle.Domain.Common;

namespace Spindle.Domain.Projects;

/// <summary>
/// Someone on a project. Private projects are open only to their members (and admins); managers can change the
/// project, its tasks and the time billed on it.
/// </summary>
public sealed class ProjectMember : Entity
{
    private ProjectMember()
    {
    }

    public Guid WorkspaceId { get; private set; }

    public Guid ProjectId { get; private set; }

    public Guid UserId { get; private set; }

    public bool IsManager { get; private set; }

    public static ProjectMember Create(Guid workspaceId, Guid projectId, Guid userId, bool isManager) => new()
    {
        WorkspaceId = Guard.Id(workspaceId),
        ProjectId = Guard.Id(projectId),
        UserId = Guard.Id(userId),
        IsManager = isManager,
    };

    public void Update(bool isManager) => IsManager = isManager;
}
