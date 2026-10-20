using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Spindle.Api.Auth;
using Spindle.Api.Common;
using Spindle.Api.Me;
using Spindle.Domain.Organizations;
using Spindle.Domain.Workspaces;
using Spindle.Infrastructure.Persistence;

namespace Spindle.Api.Workspaces;

public sealed record CreateWorkspaceRequest
{
    [Required]
    [StringLength(Workspace.NameMaxLength)]
    public string Name { get; init; } = string.Empty;
}

public sealed record DefaultWorkspaceRequest
{
    [Required]
    public Guid? WorkspaceId { get; init; }
}

internal static class WorkspaceEndpoints
{
    public static IEndpointRouteBuilder MapWorkspaceEndpoints(this IEndpointRouteBuilder api)
    {
        api.MapPost("/workspaces", CreateAsync)
            .WithName("createWorkspace")
            .WithTags("Workspaces")
            .RequireAuthorization()
            .ProducesProblem(StatusCodes.Status403Forbidden);

        api.MapPut("/me/default-workspace", SetDefaultAsync)
            .WithName("setDefaultWorkspace")
            .WithTags("Workspaces")
            .RequireAuthorization();

        return api;
    }

    /// <summary>
    /// Creates a workspace in the organization of the caller's current workspace, which the caller must own or
    /// administer. The caller becomes its admin and switches to it.
    /// </summary>
    private static async Task<Results<Created<WorkspaceSummary>, ProblemHttpResult>> CreateAsync(
        CreateWorkspaceRequest request,
        ClaimsPrincipal principal,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var userId = principal.GetUserId();
        var user = await db.Users.SingleAsync(u => u.Id == userId, cancellationToken);
        var currentOrganizationId = await db.Workspaces
            .Where(w => w.Id == user.DefaultWorkspaceId)
            .Select(w => (Guid?)w.OrganizationId)
            .SingleOrDefaultAsync(cancellationToken);

        var managed = await db.OrganizationMembers
            .Where(m => m.UserId == userId && (m.Role == OrganizationRole.Owner || m.Role == OrganizationRole.Admin))
            .Select(m => m.OrganizationId)
            .ToListAsync(cancellationToken);
        if (managed.Count == 0)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Only organization owners and admins can create workspaces.");
        }

        var organizationId = currentOrganizationId is { } current && managed.Contains(current) ? current : managed[0];
        var workspace = Workspace.Create(organizationId, request.Name);
        db.Workspaces.Add(workspace);
        db.WorkspaceMembers.Add(WorkspaceMember.Create(workspace.Id, userId, WorkspaceRole.Admin));
        user.DefaultWorkspaceId = workspace.Id;
        await db.SaveChangesAsync(cancellationToken);

        return TypedResults.Created((string?)null, new WorkspaceSummary(workspace.Id, organizationId, workspace.Name, WorkspaceRole.Admin));
    }

    /// <summary>Switches the workspace the user works in, on every device.</summary>
    private static async Task<Results<NoContent, ValidationProblem>> SetDefaultAsync(
        DefaultWorkspaceRequest request,
        ClaimsPrincipal principal,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var userId = principal.GetUserId();
        var workspaceId = request.WorkspaceId!.Value;
        if (!await db.WorkspaceMembers.AnyAsync(m => m.UserId == userId && m.WorkspaceId == workspaceId && db.Workspaces.Any(w => w.Id == workspaceId), cancellationToken))
        {
            return Problems.Validation("workspaceId", "You are not a member of that workspace.");
        }

        var user = await db.Users.SingleAsync(u => u.Id == userId, cancellationToken);
        user.DefaultWorkspaceId = workspaceId;
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }
}
