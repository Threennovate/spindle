using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Spindle.Api.Team;
using Spindle.Api.Workspaces;
using Spindle.Infrastructure.Persistence;

namespace Spindle.Tests.Integration;

/// <summary>Arranges a workspace for a test: its members and settings.</summary>
internal static class WorkspaceSetup
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Invites a new person into the admin's workspace, who then signs up through the invitation.</summary>
    public static async Task<TestUser> AddMemberAsync(this PostgresApiFactory factory, TestUser admin, string fullName = "Test User")
    {
        var email = $"member-{Guid.NewGuid():N}@example.com";
        await admin.CreateAsync<SentInvitationResponse>("/invitations", new { email });
        return await TestUser.RegisterAsync(factory, email, CapturingEmailSender.InvitationToken(factory.Emails.LastTo(email)), fullName);
    }

    /// <summary>Saves the workspace settings with some of them changed.</summary>
    public static async Task UpdateSettingsAsync(this TestUser admin, Action<Dictionary<string, object?>> change)
    {
        var current = await admin.GetAsync<WorkspaceSettingsResponse>("/settings");
        var body = new Dictionary<string, object?>
        {
            ["name"] = current.Name,
            ["currency"] = current.Currency,
            ["defaultHourlyRate"] = current.DefaultHourlyRate,
            ["onlyAdminsCreateProjects"] = current.OnlyAdminsCreateProjects,
            ["onlyAdminsCreateTags"] = current.OnlyAdminsCreateTags,
            ["lockedBefore"] = current.LockedBefore?.ToString("yyyy-MM-dd"),
            ["requireDescription"] = current.RequireDescription,
            ["requireProject"] = current.RequireProject,
            ["requireTask"] = current.RequireTask,
            ["requireTag"] = current.RequireTag,
        };
        change(body);
        var response = await admin.Client.PutAsJsonAsync(admin.In("/settings"), body, Ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
    }

    /// <summary>Ends the trial of the organization, which leaves it read-only.</summary>
    public static Task EndTrialAsync(this PostgresApiFactory factory, Guid organizationId) =>
        factory.MoveTrialEndAsync(organizationId, DateTimeOffset.UtcNow.AddMinutes(-1));

    public static async Task MoveTrialEndAsync(this PostgresApiFactory factory, Guid organizationId, DateTimeOffset endsAt)
    {
        await using var scope = factory.CreateScope();
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().OrganizationSubscriptions
            .Where(s => s.OrganizationId == organizationId)
            .ExecuteUpdateAsync(set => set.SetProperty(s => s.TrialEndsAt, endsAt), Ct);
    }
}
