namespace Spindle.Api.Workspaces;

/// <summary>Lets only admins of the route's workspace through. Runs after <see cref="WorkspaceAccessFilter"/>.</summary>
internal sealed class WorkspaceAdminFilter(CurrentWorkspace workspace) : IEndpointFilter
{
    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next) =>
        workspace.IsAdmin
            ? next(context)
            : ValueTask.FromResult<object?>(TypedResults.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Only workspace admins can do this."));
}

internal static class WorkspaceAdminExtensions
{
    public static TBuilder RequireWorkspaceAdmin<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter<TBuilder, WorkspaceAdminFilter>().ProducesProblem(StatusCodes.Status403Forbidden);
}
