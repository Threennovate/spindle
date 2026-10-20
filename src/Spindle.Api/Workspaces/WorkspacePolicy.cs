using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Spindle.Domain.Projects;
using Spindle.Domain.Workspaces;
using Spindle.Infrastructure.Persistence;

namespace Spindle.Api.Workspaces;

/// <summary>
/// Who may do what in the current workspace. Admins may do everything. Members track their own time, see public
/// projects and the private ones they belong to, manage the projects they manage, and create projects, clients and
/// tags unless the workspace reserves that for admins.
/// </summary>
internal sealed class WorkspacePolicy(AppDbContext db, CurrentWorkspace current)
{
    private Workspace? settings;

    public bool IsAdmin => current.IsAdmin;

    public async Task<Workspace> SettingsAsync(CancellationToken cancellationToken) =>
        settings ??= await db.Workspaces.AsNoTracking().SingleAsync(w => w.Id == current.Id, cancellationToken);

    public async Task<bool> CanCreateProjectsAsync(CancellationToken cancellationToken) =>
        IsAdmin || !(await SettingsAsync(cancellationToken)).OnlyAdminsCreateProjects;

    public async Task<bool> CanCreateTagsAsync(CancellationToken cancellationToken) =>
        IsAdmin || !(await SettingsAsync(cancellationToken)).OnlyAdminsCreateTags;

    /// <summary>The projects the caller can see and track time on.</summary>
    public IQueryable<Project> VisibleProjects()
    {
        var projects = db.Projects.Where(p => p.WorkspaceId == current.Id);
        return IsAdmin
            ? projects
            : projects.Where(p => !p.IsPrivate || db.ProjectMembers.Any(m => m.ProjectId == p.Id && m.UserId == current.UserId));
    }

    public async Task<bool> CanManageProjectAsync(Guid projectId, CancellationToken cancellationToken) =>
        IsAdmin || await db.ProjectMembers.AnyAsync(m => m.ProjectId == projectId && m.UserId == current.UserId && m.IsManager, cancellationToken);

    public async Task<HashSet<Guid>> ManagedProjectIdsAsync(CancellationToken cancellationToken) =>
        IsAdmin
            ? []
            : (await db.ProjectMembers.Where(m => m.WorkspaceId == current.Id && m.UserId == current.UserId && m.IsManager).Select(m => m.ProjectId).ToListAsync(cancellationToken)).ToHashSet();

    public static ProblemHttpResult Forbidden(string title) => TypedResults.Problem(statusCode: StatusCodes.Status403Forbidden, title: title);
}
