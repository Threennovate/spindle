using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Spindle.Api.Auth;
using Spindle.Api.Me;
using Spindle.Domain.Organizations;
using Spindle.Infrastructure.Persistence;

namespace Spindle.Api.Organizations;

public sealed record RenameOrganizationRequest
{
    [Required]
    [StringLength(Organization.NameMaxLength)]
    public string Name { get; init; } = string.Empty;
}

internal static class OrganizationEndpoints
{
    public static IEndpointRouteBuilder MapOrganizationEndpoints(this IEndpointRouteBuilder api)
    {
        api.MapPut("/organizations/{organizationId:guid}", RenameAsync)
            .WithName("renameOrganization")
            .WithTags("Organizations")
            .RequireAuthorization()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return api;
    }

    /// <summary>
    /// Renames the organization, for its owners and admins. A workspace still called by the organization's old name
    /// takes the new one too, as registration names both after the person and a company wants its own name on both.
    /// Allowed while the organization is read-only: a name changes no tracked time.
    /// </summary>
    private static async Task<Results<Ok<OrganizationSummary>, NotFound, ProblemHttpResult>> RenameAsync(
        Guid organizationId,
        RenameOrganizationRequest request,
        ClaimsPrincipal principal,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var userId = principal.GetUserId();
        var role = await db.OrganizationMembers
            .Where(m => m.OrganizationId == organizationId && m.UserId == userId)
            .Select(m => (OrganizationRole?)m.Role)
            .SingleOrDefaultAsync(cancellationToken);
        if (role is null)
        {
            return TypedResults.NotFound();
        }
        if (role == OrganizationRole.Member)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Only the organization's owners and admins can rename it.");
        }

        var organization = await db.Organizations.SingleAsync(o => o.Id == organizationId, cancellationToken);
        var oldName = organization.Name;
        organization.Rename(request.Name);
        var workspaces = await db.Workspaces
            .Where(w => w.OrganizationId == organizationId && w.Name == oldName)
            .ToListAsync(cancellationToken);
        foreach (var workspace in workspaces)
        {
            workspace.Rename(organization.Name);
        }

        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(new OrganizationSummary(organization.Id, organization.Name, role.Value));
    }
}
