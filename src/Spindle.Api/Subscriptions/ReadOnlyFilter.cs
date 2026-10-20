using Microsoft.EntityFrameworkCore;
using Spindle.Api.Workspaces;
using Spindle.Domain.Subscriptions;
using Spindle.Infrastructure.Persistence;

namespace Spindle.Api.Subscriptions;

/// <summary>
/// Refuses changes in a workspace whose organization is read-only, because its trial or plan has ended. Reading,
/// reports and exports go on. Runs after <see cref="WorkspaceAccessFilter"/>, so strangers still get 404.
/// </summary>
internal sealed class ReadOnlyFilter(AppDbContext db, OrganizationSubscriptions subscriptions, CurrentWorkspace workspace) : IEndpointFilter
{
    /// <summary>The problem's <c>type</c>, which the apps recognise.</summary>
    public const string ProblemType = "urn:spindle:problem:read-only";

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        if (!subscriptions.Enabled
            || HttpMethods.IsGet(http.Request.Method)
            || HttpMethods.IsHead(http.Request.Method)
            || http.GetEndpoint()?.Metadata.GetMetadata<AllowedWhenReadOnly>() is not null)
        {
            return await next(context);
        }

        if (await ReadOnlyOrganizationAsync(db, subscriptions, workspace.Id, http.RequestAborted) is not { } organizationId)
        {
            return await next(context);
        }

        return TypedResults.Problem(
            statusCode: StatusCodes.Status402PaymentRequired,
            type: ProblemType,
            title: "This organization is read-only.",
            detail: "Its trial or plan has ended. Everything in it can still be read, reported and exported, but not changed.",
            extensions: new Dictionary<string, object?> { ["organizationId"] = organizationId });
    }

    /// <summary>The workspace's organization when it is read-only, or null when it may change things.</summary>
    public static async Task<Guid?> ReadOnlyOrganizationAsync(AppDbContext db, OrganizationSubscriptions subscriptions, Guid workspaceId, CancellationToken cancellationToken)
    {
        if (!subscriptions.Enabled)
        {
            return null;
        }

        var organizationId = await db.Workspaces.Where(w => w.Id == workspaceId).Select(w => w.OrganizationId).SingleAsync(cancellationToken);
        var standing = (await subscriptions.StandingsAsync([organizationId], cancellationToken))[organizationId];
        return standing.Access == OrganizationAccess.Full ? null : organizationId;
    }
}

/// <summary>Marks a change that is allowed in a read-only organization.</summary>
internal sealed class AllowedWhenReadOnly;

internal static class ReadOnlyExtensions
{
    /// <summary>
    /// Lets this change through in a read-only organization: one that only reads despite its method (reports), ends
    /// something (stopping the timer), lowers the seats to pay for, or concerns only the person's own agent activity.
    /// </summary>
    public static TBuilder AllowWhenReadOnly<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new AllowedWhenReadOnly());
}
