using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Spindle.Api.Auth;
using Spindle.Infrastructure.Persistence;

namespace Spindle.Api.Sync;

/// <summary>
/// Something changed that the apps show, so they should load it again. It says what changed, not how, so the
/// apps keep reading data through the API and its checks.
/// </summary>
/// <param name="Topic">
/// <c>entries</c> (someone's time entries), <c>catalog</c> (projects, tasks, clients, tags), <c>team</c> (members,
/// groups, invitations), <c>settings</c> (the workspace), <c>me</c> (the person's account), <c>savedReports</c>, or
/// <c>billing</c> (time billed on projects whose billed time is entered separately), or <c>agentActivity</c> (the
/// person's repository links, dismissed split suggestions and uploaded agent activity), or <c>agentProposals</c>
/// (entries and changes the person's agents proposed, in the workspace named). Rates belong to where they are
/// set: <c>catalog</c>, <c>team</c> or <c>settings</c>.
/// </param>
/// <param name="UserId">Whose entries or account changed, where that matters.</param>
public sealed record SyncChange(string Topic, Guid? WorkspaceId, Guid? UserId);

public interface ISyncClient
{
    Task Changed(SyncChange change);
}

/// <summary>
/// Tells the apps when their data changes elsewhere: on another device, or by a teammate. Each connection hears
/// about its own account, and about the workspaces it watches.
/// </summary>
[Authorize]
public sealed class SyncHub(AppDbContext db) : Hub<ISyncClient>
{
    public const string Path = "/api/hubs/sync";

    public override async Task OnConnectedAsync()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, SyncGroups.User(Context.User!.GetUserId()));
        await base.OnConnectedAsync();
    }

    /// <summary>Follows a workspace, usually the one on screen. Only its members may; returns whether it worked.</summary>
    public async Task<bool> Watch(Guid workspaceId)
    {
        var userId = Context.User!.GetUserId();
        if (!await db.WorkspaceMembers.AnyAsync(m => m.WorkspaceId == workspaceId && m.UserId == userId, Context.ConnectionAborted))
        {
            return false;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, SyncGroups.Workspace(workspaceId), Context.ConnectionAborted);
        return true;
    }

    /// <summary>Stops following a workspace, e.g. after switching to another.</summary>
    public Task Unwatch(Guid workspaceId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, SyncGroups.Workspace(workspaceId), Context.ConnectionAborted);
}

internal static class SyncGroups
{
    public static string User(Guid userId) => $"user:{userId}";

    public static string Workspace(Guid workspaceId) => $"workspace:{workspaceId}";
}
