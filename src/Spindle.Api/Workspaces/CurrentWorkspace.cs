using Microsoft.EntityFrameworkCore;
using Spindle.Api.Auth;
using Spindle.Api.Subscriptions;
using Spindle.Domain.Workspaces;
using Spindle.Infrastructure.Persistence;

namespace Spindle.Api.Workspaces;

/// <summary>
/// The workspace in the request's route and the caller's role in it. Filled in by <see cref="WorkspaceAccessFilter"/>
/// before the handler runs, so handlers can rely on the caller being a member.
/// </summary>
internal sealed class CurrentWorkspace
{
    private (Guid Id, Guid UserId, WorkspaceRole Role)? access;

    public Guid Id => Access.Id;

    public Guid UserId => Access.UserId;

    public WorkspaceRole Role => Access.Role;

    public bool IsAdmin => Role == WorkspaceRole.Admin;

    private (Guid Id, Guid UserId, WorkspaceRole Role) Access =>
        access ?? throw new InvalidOperationException("The endpoint is not behind WorkspaceAccessFilter.");

    public void Set(Guid id, Guid userId, WorkspaceRole role) => access = (id, userId, role);
}

/// <summary>Lets only members of the workspace in the route through. To anyone else the workspace does not exist.</summary>
internal sealed class WorkspaceAccessFilter(AppDbContext db, CurrentWorkspace current) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        if (!Guid.TryParse(http.Request.RouteValues["workspaceId"]?.ToString(), out var workspaceId))
        {
            return TypedResults.NotFound();
        }

        var userId = http.User.GetUserId();
        var role = await db.WorkspaceMembers
            .Where(m => m.WorkspaceId == workspaceId && m.UserId == userId && db.Workspaces.Any(w => w.Id == workspaceId))
            .Select(m => (WorkspaceRole?)m.Role)
            .SingleOrDefaultAsync(http.RequestAborted);

        if (role is null)
        {
            return TypedResults.NotFound();
        }

        current.Set(workspaceId, userId, role.Value);
        return await next(context);
    }
}

internal static class WorkspaceRoutes
{
    /// <summary>The group for everything inside a workspace: <c>/api/workspaces/{workspaceId}/…</c>.</summary>
    public static RouteGroupBuilder MapWorkspaceGroup(this IEndpointRouteBuilder api) =>
        api.MapGroup("/workspaces/{workspaceId:guid}")
            .RequireAuthorization()
            .AddEndpointFilter<WorkspaceAccessFilter>()
            .AddEndpointFilter<ReadOnlyFilter>()
            .ProducesProblem(StatusCodes.Status404NotFound);
}
