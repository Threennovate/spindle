using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Spindle.Api.Billing;
using Spindle.Api.Common;
using Spindle.Api.Subscriptions;
using Spindle.Api.Workspaces;
using Spindle.Domain.Billing;
using Spindle.Domain.Organizations;
using Spindle.Domain.Workspaces;
using Spindle.Infrastructure.Persistence;

namespace Spindle.Api.Team;

/// <param name="HourlyRate">What the member's time is billed at, as last set; shown to admins only.</param>
/// <param name="LaborCost">What an hour of the member's time costs, as last set; shown to admins only.</param>
public sealed record MemberResponse(
    Guid UserId,
    string FullName,
    string Email,
    WorkspaceRole Role,
    DateTimeOffset JoinedAt,
    decimal? HourlyRate,
    decimal? LaborCost);

/// <summary>
/// A member's role and rates. Rates left out are cleared. A changed rate applies from its date on, if one is given,
/// and earlier time keeps the rate it had; without a date the current rate is corrected for all the time it covers.
/// </summary>
public sealed record UpdateMemberRequest
{
    [Required]
    public WorkspaceRole? Role { get; init; }

    [Range(0, 1_000_000)]
    public decimal? HourlyRate { get; init; }

    public DateOnly? HourlyRateFrom { get; init; }

    [Range(0, 1_000_000)]
    public decimal? LaborCost { get; init; }

    public DateOnly? LaborCostFrom { get; init; }
}

internal static class MemberEndpoints
{
    private const string LastAdmin = "A workspace needs at least one admin.";

    public static IEndpointRouteBuilder MapMemberEndpoints(this IEndpointRouteBuilder workspace)
    {
        var group = workspace.MapGroup("/members").WithTags("Team");

        group.MapGet("", ListAsync).WithName("listMembers");
        group.MapPut("/{userId:guid}", UpdateAsync).WithName("updateMember").RequireWorkspaceAdmin();
        group.MapDelete("/{userId:guid}", RemoveAsync).WithName("removeMember").AllowWhenReadOnly().RequireWorkspaceAdmin();

        return workspace;
    }

    private static async Task<Ok<List<MemberResponse>>> ListAsync(CurrentWorkspace workspace, AppDbContext db, RateBook rates, CancellationToken cancellationToken) =>
        TypedResults.Ok(await QueryAsync(db, rates, workspace, userId: null, cancellationToken));

    private static async Task<Results<Ok<MemberResponse>, NotFound, ValidationProblem>> UpdateAsync(
        Guid userId,
        UpdateMemberRequest request,
        CurrentWorkspace workspace,
        AppDbContext db,
        RateBook rates,
        CancellationToken cancellationToken)
    {
        var member = await db.WorkspaceMembers.SingleOrDefaultAsync(m => m.WorkspaceId == workspace.Id && m.UserId == userId, cancellationToken);
        if (member is null)
        {
            return TypedResults.NotFound();
        }

        var role = request.Role!.Value;
        if (member.Role == WorkspaceRole.Admin && role != WorkspaceRole.Admin && await IsLastAdminAsync(db, workspace.Id, cancellationToken))
        {
            return Problems.Validation("role", LastAdmin);
        }

        member.ChangeRole(role);
        await rates.SetAsync(RateScope.Member(userId), request.HourlyRate, request.HourlyRateFrom, "hourlyRate", "hourlyRateFrom", cancellationToken);
        await rates.SetAsync(RateScope.LaborCost(userId), request.LaborCost, request.LaborCostFrom, "laborCost", "laborCostFrom", cancellationToken);
        await db.SaveChangesAsync(cancellationToken);

        return TypedResults.Ok((await QueryAsync(db, rates, workspace, userId, cancellationToken)).Single());
    }

    /// <summary>Takes the user out of the workspace. The time they tracked there stays, for reports.</summary>
    private static async Task<Results<NoContent, NotFound, ValidationProblem>> RemoveAsync(
        Guid userId,
        CurrentWorkspace workspace,
        AppDbContext db,
        SeatChanges seatChanges,
        CancellationToken cancellationToken)
    {
        var member = await db.WorkspaceMembers.SingleOrDefaultAsync(m => m.WorkspaceId == workspace.Id && m.UserId == userId, cancellationToken);
        if (member is null)
        {
            return TypedResults.NotFound();
        }

        if (member.Role == WorkspaceRole.Admin && await IsLastAdminAsync(db, workspace.Id, cancellationToken))
        {
            return Problems.Validation("userId", LastAdmin);
        }

        db.WorkspaceMembers.Remove(member);
        db.ProjectMembers.RemoveRange(await db.ProjectMembers.Where(m => m.WorkspaceId == workspace.Id && m.UserId == userId).ToListAsync(cancellationToken));
        db.GroupMembers.RemoveRange(await db.GroupMembers
            .Where(m => m.UserId == userId && db.Groups.Any(g => g.Id == m.GroupId && g.WorkspaceId == workspace.Id))
            .ToListAsync(cancellationToken));

        // Someone removed from their current workspace continues in another one of theirs.
        var user = await db.Users.SingleAsync(u => u.Id == userId, cancellationToken);
        if (user.DefaultWorkspaceId == workspace.Id)
        {
            user.DefaultWorkspaceId = await db.WorkspaceMembers
                .Where(m => m.UserId == userId && m.WorkspaceId != workspace.Id)
                .Select(m => (Guid?)m.WorkspaceId)
                .FirstOrDefaultAsync(cancellationToken);
        }

        // Out of the organization's last workspace, a plain member leaves the organization too. Owners and
        // organization admins stay, since they run it without needing a workspace.
        var organizationId = await db.Workspaces.Where(w => w.Id == workspace.Id).Select(w => w.OrganizationId).SingleAsync(cancellationToken);
        var stillIn = await db.WorkspaceMembers.AnyAsync(
            m => m.UserId == userId && m.WorkspaceId != workspace.Id && db.Workspaces.Any(w => w.Id == m.WorkspaceId && w.OrganizationId == organizationId),
            cancellationToken);
        if (!stillIn)
        {
            db.OrganizationMembers.RemoveRange(await db.OrganizationMembers
                .Where(m => m.OrganizationId == organizationId && m.UserId == userId && m.Role == OrganizationRole.Member)
                .ToListAsync(cancellationToken));
        }

        await db.SaveChangesAsync(cancellationToken);
        seatChanges.Changed(organizationId);
        return TypedResults.NoContent();
    }

    /// <summary>Members of the workspace; rates are shown to admins only.</summary>
    private static async Task<List<MemberResponse>> QueryAsync(
        AppDbContext db,
        RateBook rates,
        CurrentWorkspace workspace,
        Guid? userId,
        CancellationToken cancellationToken)
    {
        var card = workspace.IsAdmin ? await rates.CardAsync(cancellationToken) : RateCard.Empty;
        var members = await (
            from member in db.WorkspaceMembers
            join user in db.Users on member.UserId equals user.Id
            where member.WorkspaceId == workspace.Id && (userId == null || member.UserId == userId)
            orderby user.FullName
            select new { user.Id, user.FullName, user.Email, member.Role, member.CreatedAt })
            .ToListAsync(cancellationToken);

        return members
            .Select(m => new MemberResponse(
                m.Id,
                m.FullName,
                m.Email!,
                m.Role,
                m.CreatedAt,
                card.Current(RateScope.Member(m.Id)),
                card.Current(RateScope.LaborCost(m.Id))))
            .ToList();
    }

    private static async Task<bool> IsLastAdminAsync(AppDbContext db, Guid workspaceId, CancellationToken cancellationToken) =>
        await db.WorkspaceMembers.CountAsync(m => m.WorkspaceId == workspaceId && m.Role == WorkspaceRole.Admin, cancellationToken) <= 1;
}
