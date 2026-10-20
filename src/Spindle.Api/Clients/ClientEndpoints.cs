using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Spindle.Api.Common;
using Spindle.Api.Workspaces;
using Spindle.Domain.Clients;
using Spindle.Infrastructure.Persistence;

namespace Spindle.Api.Clients;

public sealed record ClientResponse(Guid Id, string Name, DateTimeOffset? ArchivedAt, int ActiveProjectCount);

public sealed record ClientRequest
{
    [Required]
    [StringLength(Client.NameMaxLength)]
    public string Name { get; init; } = string.Empty;
}

internal static class ClientEndpoints
{
    private const string NameTaken = "A client with this name already exists.";

    public static IEndpointRouteBuilder MapClientEndpoints(this IEndpointRouteBuilder workspace)
    {
        var group = workspace.MapGroup("/clients").WithTags("Clients");

        group.MapGet("", ListAsync).WithName("listClients");
        group.MapPost("", CreateAsync).WithName("createClient");
        group.MapPut("/{clientId:guid}", UpdateAsync).WithName("updateClient").RequireWorkspaceAdmin();
        group.MapPost("/{clientId:guid}/archive", ArchiveAsync).WithName("archiveClient").RequireWorkspaceAdmin();
        group.MapPost("/{clientId:guid}/restore", RestoreAsync).WithName("restoreClient").RequireWorkspaceAdmin();
        group.MapDelete("/{clientId:guid}", DeleteAsync).WithName("deleteClient").RequireWorkspaceAdmin();

        return workspace;
    }

    private static async Task<Ok<List<ClientResponse>>> ListAsync(
        CurrentWorkspace workspace,
        AppDbContext db,
        CancellationToken cancellationToken,
        bool includeArchived = false)
    {
        var clients = await ToResponses(db, db.Clients
                .Where(c => c.WorkspaceId == workspace.Id && (includeArchived || c.ArchivedAt == null))
                .OrderBy(c => c.Name))
            .ToListAsync(cancellationToken);

        return TypedResults.Ok(clients);
    }

    private static async Task<Results<Created<ClientResponse>, ValidationProblem, ProblemHttpResult>> CreateAsync(
        ClientRequest request,
        CurrentWorkspace workspace,
        AppDbContext db,
        WorkspacePolicy policy,
        CancellationToken cancellationToken)
    {
        if (!await policy.CanCreateProjectsAsync(cancellationToken))
        {
            return WorkspacePolicy.Forbidden("Only admins can create clients in this workspace.");
        }

        var client = Client.Create(workspace.Id, request.Name);
        db.Clients.Add(client);

        if (!await db.TrySaveChangesAsync(cancellationToken))
        {
            return Problems.Validation("name", NameTaken);
        }

        return TypedResults.Created((string?)null, await GetAsync(db, workspace.Id, client.Id, cancellationToken));
    }

    private static async Task<Results<Ok<ClientResponse>, NotFound, ValidationProblem>> UpdateAsync(
        Guid clientId,
        ClientRequest request,
        CurrentWorkspace workspace,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var client = await FindAsync(db, workspace.Id, clientId, cancellationToken);
        if (client is null)
        {
            return TypedResults.NotFound();
        }

        client.Rename(request.Name);
        if (!await db.TrySaveChangesAsync(cancellationToken))
        {
            return Problems.Validation("name", NameTaken);
        }

        return TypedResults.Ok(await GetAsync(db, workspace.Id, clientId, cancellationToken));
    }

    private static Task<Results<Ok<ClientResponse>, NotFound>> ArchiveAsync(
        Guid clientId,
        CurrentWorkspace workspace,
        AppDbContext db,
        TimeProvider timeProvider,
        CancellationToken cancellationToken) =>
        ChangeAsync(db, workspace.Id, clientId, client => client.Archive(timeProvider.GetUtcNow()), cancellationToken);

    private static Task<Results<Ok<ClientResponse>, NotFound>> RestoreAsync(
        Guid clientId,
        CurrentWorkspace workspace,
        AppDbContext db,
        CancellationToken cancellationToken) =>
        ChangeAsync(db, workspace.Id, clientId, client => client.Restore(), cancellationToken);

    /// <summary>Deletes the client. Its projects stay, without a client.</summary>
    private static async Task<Results<NoContent, NotFound, ValidationProblem>> DeleteAsync(
        Guid clientId,
        CurrentWorkspace workspace,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var client = await FindAsync(db, workspace.Id, clientId, cancellationToken);
        if (client is null)
        {
            return TypedResults.NotFound();
        }

        foreach (var project in await db.Projects.Where(p => p.ClientId == clientId).ToListAsync(cancellationToken))
        {
            project.RemoveClient();
        }

        db.Clients.Remove(client);
        if (!await db.TrySaveChangesAsync(cancellationToken))
        {
            return Problems.Validation(
                "clientId",
                "A project of this client has the same name as a project without a client. Rename one of them first.");
        }

        return TypedResults.NoContent();
    }

    private static async Task<Results<Ok<ClientResponse>, NotFound>> ChangeAsync(
        AppDbContext db,
        Guid workspaceId,
        Guid clientId,
        Action<Client> change,
        CancellationToken cancellationToken)
    {
        var client = await FindAsync(db, workspaceId, clientId, cancellationToken);
        if (client is null)
        {
            return TypedResults.NotFound();
        }

        change(client);
        await db.SaveChangesAsync(cancellationToken);
        return TypedResults.Ok(await GetAsync(db, workspaceId, clientId, cancellationToken));
    }

    private static Task<Client?> FindAsync(AppDbContext db, Guid workspaceId, Guid clientId, CancellationToken cancellationToken) =>
        db.Clients.SingleOrDefaultAsync(c => c.Id == clientId && c.WorkspaceId == workspaceId, cancellationToken);

    private static Task<ClientResponse> GetAsync(AppDbContext db, Guid workspaceId, Guid clientId, CancellationToken cancellationToken) =>
        ToResponses(db, db.Clients.Where(c => c.Id == clientId && c.WorkspaceId == workspaceId)).SingleAsync(cancellationToken);

    // Filter and sort before projecting: EF cannot translate conditions on the constructed response.
    private static IQueryable<ClientResponse> ToResponses(AppDbContext db, IQueryable<Client> clients) =>
        clients.AsNoTracking()
            .Select(c => new ClientResponse(
                c.Id,
                c.Name,
                c.ArchivedAt,
                db.Projects.Count(p => p.ClientId == c.Id && p.ArchivedAt == null)));
}
