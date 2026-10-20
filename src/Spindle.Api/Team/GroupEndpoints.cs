using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Spindle.Api.Common;
using Spindle.Api.Workspaces;
using Spindle.Domain.Groups;
using Spindle.Infrastructure.Persistence;

namespace Spindle.Api.Team;

public sealed record GroupResponse(Guid Id, string Name, IReadOnlyList<Guid> MemberIds);

/// <summary>A group's name and everyone in it; members not listed are taken out.</summary>
public sealed record GroupRequest
{
    [Required]
    [StringLength(Group.NameMaxLength)]
    public string Name { get; init; } = string.Empty;

    [MaxLength(1000)]
    public Guid[] MemberIds { get; init; } = [];
}

internal static class GroupEndpoints
{
    private const string NameTaken = "A group with this name already exists.";

    public static IEndpointRouteBuilder MapGroupEndpoints(this IEndpointRouteBuilder workspace)
    {
        var group = workspace.MapGroup("/groups").WithTags("Team");

        group.MapGet("", ListAsync).WithName("listGroups");
        group.MapPost("", CreateAsync).WithName("createGroup").RequireWorkspaceAdmin();
        group.MapPut("/{groupId:guid}", UpdateAsync).WithName("updateGroup").RequireWorkspaceAdmin();
        group.MapDelete("/{groupId:guid}", DeleteAsync).WithName("deleteGroup").RequireWorkspaceAdmin();

        return workspace;
    }

    private static async Task<Ok<List<GroupResponse>>> ListAsync(CurrentWorkspace workspace, AppDbContext db, CancellationToken cancellationToken) =>
        TypedResults.Ok(await QueryAsync(db, workspace.Id, groupId: null, cancellationToken));

    private static async Task<Results<Created<GroupResponse>, ValidationProblem>> CreateAsync(
        GroupRequest request,
        CurrentWorkspace workspace,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        if (await InvalidMembersAsync(db, workspace.Id, request.MemberIds, cancellationToken) is { } invalid)
        {
            return invalid;
        }

        var group = Group.Create(workspace.Id, request.Name);
        db.Groups.Add(group);
        db.GroupMembers.AddRange(request.MemberIds.Distinct().Select(userId => GroupMember.Create(group.Id, userId)));
        if (!await db.TrySaveChangesAsync(cancellationToken))
        {
            return Problems.Validation("name", NameTaken);
        }

        return TypedResults.Created((string?)null, (await QueryAsync(db, workspace.Id, group.Id, cancellationToken)).Single());
    }

    private static async Task<Results<Ok<GroupResponse>, NotFound, ValidationProblem>> UpdateAsync(
        Guid groupId,
        GroupRequest request,
        CurrentWorkspace workspace,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var group = await db.Groups.SingleOrDefaultAsync(g => g.Id == groupId && g.WorkspaceId == workspace.Id, cancellationToken);
        if (group is null)
        {
            return TypedResults.NotFound();
        }

        if (await InvalidMembersAsync(db, workspace.Id, request.MemberIds, cancellationToken) is { } invalid)
        {
            return invalid;
        }

        group.Rename(request.Name);
        var wanted = request.MemberIds.ToHashSet();
        var current = await db.GroupMembers.Where(m => m.GroupId == groupId).ToListAsync(cancellationToken);
        db.GroupMembers.RemoveRange(current.Where(m => !wanted.Contains(m.UserId)));
        db.GroupMembers.AddRange(wanted.Except(current.Select(m => m.UserId)).Select(userId => GroupMember.Create(groupId, userId)));

        if (!await db.TrySaveChangesAsync(cancellationToken))
        {
            return Problems.Validation("name", NameTaken);
        }

        return TypedResults.Ok((await QueryAsync(db, workspace.Id, groupId, cancellationToken)).Single());
    }

    private static async Task<Results<NoContent, NotFound>> DeleteAsync(
        Guid groupId,
        CurrentWorkspace workspace,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var group = await db.Groups.SingleOrDefaultAsync(g => g.Id == groupId && g.WorkspaceId == workspace.Id, cancellationToken);
        if (group is null)
        {
            return TypedResults.NotFound();
        }

        db.GroupMembers.RemoveRange(await db.GroupMembers.Where(m => m.GroupId == groupId).ToListAsync(cancellationToken));
        db.Groups.Remove(group);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.NoContent();
    }

    private static async Task<ValidationProblem?> InvalidMembersAsync(AppDbContext db, Guid workspaceId, Guid[] memberIds, CancellationToken cancellationToken)
    {
        var ids = memberIds.Distinct().ToArray();
        var found = await db.WorkspaceMembers.CountAsync(m => m.WorkspaceId == workspaceId && ids.Contains(m.UserId), cancellationToken);
        return found == ids.Length ? null : Problems.Validation("memberIds", "Only members of the workspace can be in its groups.");
    }

    private static async Task<List<GroupResponse>> QueryAsync(AppDbContext db, Guid workspaceId, Guid? groupId, CancellationToken cancellationToken)
    {
        var groups = await db.Groups.AsNoTracking()
            .Where(g => g.WorkspaceId == workspaceId && (groupId == null || g.Id == groupId))
            .OrderBy(g => g.Name)
            .ToListAsync(cancellationToken);
        var ids = groups.Select(g => g.Id).ToList();
        var members = (await db.GroupMembers.AsNoTracking().Where(m => ids.Contains(m.GroupId)).ToListAsync(cancellationToken)).ToLookup(m => m.GroupId);

        return groups.Select(g => new GroupResponse(g.Id, g.Name, members[g.Id].Select(m => m.UserId).ToList())).ToList();
    }
}
