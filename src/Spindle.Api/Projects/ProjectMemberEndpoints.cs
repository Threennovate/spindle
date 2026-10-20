using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Spindle.Api.Billing;
using Spindle.Api.Common;
using Spindle.Api.Workspaces;
using Spindle.Domain.Billing;
using Spindle.Domain.Projects;
using Spindle.Infrastructure.Persistence;

namespace Spindle.Api.Projects;

/// <param name="HourlyRate">The member's rate on this project, as last set; shown to admins only.</param>
public sealed record ProjectMemberResponse(Guid UserId, string FullName, string Email, bool IsManager, decimal? HourlyRate);

public sealed record ProjectMemberRequest
{
    public bool IsManager { get; init; }

    /// <summary>Only admins can set it; for others it stays as it is.</summary>
    [System.ComponentModel.DataAnnotations.Range(0, 1_000_000)]
    public decimal? HourlyRate { get; init; }

    /// <summary>
    /// The first day a changed rate applies to; earlier time keeps the rate it had. Left out, the current rate is
    /// corrected for all the time it covers.
    /// </summary>
    public DateOnly? HourlyRateFrom { get; init; }
}

internal static class ProjectMemberEndpoints
{
    private const string CannotManage = "Only admins and the project's managers can change who is on it.";

    public static IEndpointRouteBuilder MapProjectMemberEndpoints(this IEndpointRouteBuilder workspace)
    {
        var group = workspace.MapGroup("/projects/{projectId:guid}/members").WithTags("Projects");

        group.MapGet("", ListAsync).WithName("listProjectMembers");
        group.MapPut("/{userId:guid}", SetAsync).WithName("setProjectMember");
        group.MapDelete("/{userId:guid}", RemoveAsync).WithName("removeProjectMember");

        return workspace;
    }

    private static async Task<Results<Ok<List<ProjectMemberResponse>>, NotFound>> ListAsync(
        Guid projectId,
        AppDbContext db,
        WorkspacePolicy policy,
        RateBook rates,
        CancellationToken cancellationToken)
    {
        if (!await policy.VisibleProjects().AnyAsync(p => p.Id == projectId, cancellationToken))
        {
            return TypedResults.NotFound();
        }

        return TypedResults.Ok(await QueryAsync(db, policy, rates, projectId, userId: null, cancellationToken));
    }

    /// <summary>Adds someone from the workspace to the project, or changes their part in it.</summary>
    private static async Task<Results<Ok<ProjectMemberResponse>, NotFound, ValidationProblem, ProblemHttpResult>> SetAsync(
        Guid projectId,
        Guid userId,
        ProjectMemberRequest request,
        CurrentWorkspace workspace,
        AppDbContext db,
        WorkspacePolicy policy,
        RateBook rates,
        CancellationToken cancellationToken)
    {
        if (!await policy.VisibleProjects().AnyAsync(p => p.Id == projectId, cancellationToken))
        {
            return TypedResults.NotFound();
        }

        if (!await policy.CanManageProjectAsync(projectId, cancellationToken))
        {
            return WorkspacePolicy.Forbidden(CannotManage);
        }

        if (!await db.WorkspaceMembers.AnyAsync(m => m.WorkspaceId == workspace.Id && m.UserId == userId, cancellationToken))
        {
            return Problems.Validation("userId", "Only members of the workspace can be added to its projects.");
        }

        var member = await db.ProjectMembers.SingleOrDefaultAsync(m => m.ProjectId == projectId && m.UserId == userId, cancellationToken);
        if (member is null)
        {
            db.ProjectMembers.Add(ProjectMember.Create(workspace.Id, projectId, userId, request.IsManager));
        }
        else
        {
            member.Update(request.IsManager);
        }

        // Rates are admin business: for anyone else the member keeps the rate they had.
        if (policy.IsAdmin)
        {
            await rates.SetAsync(RateScope.ProjectMember(projectId, userId), request.HourlyRate, request.HourlyRateFrom, "hourlyRate", "hourlyRateFrom", cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok((await QueryAsync(db, policy, rates, projectId, userId, cancellationToken)).Single());
    }

    /// <summary>Takes someone off the project. Their rate here ends after today; their time so far keeps it.</summary>
    private static async Task<Results<NoContent, NotFound, ProblemHttpResult>> RemoveAsync(
        Guid projectId,
        Guid userId,
        AppDbContext db,
        WorkspacePolicy policy,
        RateBook rates,
        CancellationToken cancellationToken)
    {
        if (!await policy.VisibleProjects().AnyAsync(p => p.Id == projectId, cancellationToken))
        {
            return TypedResults.NotFound();
        }

        if (!await policy.CanManageProjectAsync(projectId, cancellationToken))
        {
            return WorkspacePolicy.Forbidden(CannotManage);
        }

        var member = await db.ProjectMembers.SingleOrDefaultAsync(m => m.ProjectId == projectId && m.UserId == userId, cancellationToken);
        if (member is null)
        {
            return TypedResults.NotFound();
        }

        db.ProjectMembers.Remove(member);
        await rates.EndAsync(RateScope.ProjectMember(projectId, userId), cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<List<ProjectMemberResponse>> QueryAsync(
        AppDbContext db,
        WorkspacePolicy policy,
        RateBook rates,
        Guid projectId,
        Guid? userId,
        CancellationToken cancellationToken)
    {
        var card = policy.IsAdmin ? await rates.CardAsync(cancellationToken) : RateCard.Empty;
        var members = await (
            from member in db.ProjectMembers
            join user in db.Users on member.UserId equals user.Id
            where member.ProjectId == projectId && (userId == null || member.UserId == userId)
            orderby user.FullName
            select new { user.Id, user.FullName, user.Email, member.IsManager })
            .ToListAsync(cancellationToken);

        return members
            .Select(m => new ProjectMemberResponse(m.Id, m.FullName, m.Email!, m.IsManager, card.Current(RateScope.ProjectMember(projectId, m.Id))))
            .ToList();
    }
}
