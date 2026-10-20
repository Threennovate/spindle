using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Spindle.Api.Common;
using Spindle.Api.Workspaces;
using Spindle.Domain.Tags;
using Spindle.Infrastructure.Persistence;

namespace Spindle.Api.Tags;

public sealed record TagResponse(Guid Id, string Name);

public sealed record TagRequest
{
    [Required]
    [StringLength(Tag.NameMaxLength)]
    public string Name { get; init; } = string.Empty;
}

internal static class TagEndpoints
{
    private const string NameTaken = "A tag with this name already exists.";

    public static IEndpointRouteBuilder MapTagEndpoints(this IEndpointRouteBuilder workspace)
    {
        var group = workspace.MapGroup("/tags").WithTags("Tags");

        group.MapGet("", ListAsync).WithName("listTags");
        group.MapPost("", CreateAsync).WithName("createTag");
        group.MapPut("/{tagId:guid}", UpdateAsync).WithName("updateTag").RequireWorkspaceAdmin();
        group.MapDelete("/{tagId:guid}", DeleteAsync).WithName("deleteTag").RequireWorkspaceAdmin();

        return workspace;
    }

    private static async Task<Ok<List<TagResponse>>> ListAsync(
        CurrentWorkspace workspace,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var tags = await db.Tags.AsNoTracking()
            .Where(t => t.WorkspaceId == workspace.Id)
            .OrderBy(t => t.Name)
            .Select(t => new TagResponse(t.Id, t.Name))
            .ToListAsync(cancellationToken);

        return TypedResults.Ok(tags);
    }

    private static async Task<Results<Created<TagResponse>, ValidationProblem, ProblemHttpResult>> CreateAsync(
        TagRequest request,
        CurrentWorkspace workspace,
        AppDbContext db,
        WorkspacePolicy policy,
        CancellationToken cancellationToken)
    {
        if (!await policy.CanCreateTagsAsync(cancellationToken))
        {
            return WorkspacePolicy.Forbidden("Only admins can create tags in this workspace.");
        }

        var tag = Tag.Create(workspace.Id, request.Name);
        db.Tags.Add(tag);

        return await db.TrySaveChangesAsync(cancellationToken)
            ? TypedResults.Created((string?)null, new TagResponse(tag.Id, tag.Name))
            : Problems.Validation("name", NameTaken);
    }

    private static async Task<Results<Ok<TagResponse>, NotFound, ValidationProblem>> UpdateAsync(
        Guid tagId,
        TagRequest request,
        CurrentWorkspace workspace,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var tag = await db.Tags.SingleOrDefaultAsync(t => t.Id == tagId && t.WorkspaceId == workspace.Id, cancellationToken);
        if (tag is null)
        {
            return TypedResults.NotFound();
        }

        tag.Rename(request.Name);
        return await db.TrySaveChangesAsync(cancellationToken)
            ? TypedResults.Ok(new TagResponse(tag.Id, tag.Name))
            : Problems.Validation("name", NameTaken);
    }

    /// <summary>Deletes the tag and takes it off every time entry that had it.</summary>
    private static async Task<Results<NoContent, NotFound>> DeleteAsync(
        Guid tagId,
        CurrentWorkspace workspace,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var deleted = await db.InTransactionAsync(async ct =>
        {
            var tag = await db.Tags.SingleOrDefaultAsync(t => t.Id == tagId && t.WorkspaceId == workspace.Id, ct);
            if (tag is null)
            {
                return false;
            }

            foreach (var entry in await db.TimeEntries.Where(e => e.WorkspaceId == workspace.Id && e.TagIds.Contains(tagId)).ToListAsync(ct))
            {
                entry.RemoveTag(tagId);
            }

            db.Tags.Remove(tag);
            await db.SaveChangesAsync(ct);
            return true;
        }, cancellationToken);

        return deleted ? TypedResults.NoContent() : TypedResults.NotFound();
    }
}
