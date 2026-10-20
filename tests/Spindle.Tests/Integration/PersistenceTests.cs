using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Spindle.Domain.Organizations;
using Spindle.Domain.Workspaces;
using Spindle.Infrastructure.Identity;
using Spindle.Infrastructure.Persistence;

namespace Spindle.Tests.Integration;

[Collection(PostgresCollection.Name)]
public sealed class PersistenceTests(PostgresApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_model_has_no_changes_missing_from_the_migrations()
    {
        await using var scope = factory.CreateScope();

        Assert.False(Db(scope).Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task Writes_stamp_timestamps_and_change_the_concurrency_token()
    {
        var organization = Organization.Create("Before");
        await SaveAsync(db => db.Add(organization));
        var created = await FindOrganizationAsync(organization.Id);

        await SaveAsync(async db => (await db.Organizations.SingleAsync(o => o.Id == organization.Id, Ct)).Rename("After"));
        var updated = await FindOrganizationAsync(organization.Id);

        Assert.NotEqual(default, created.CreatedAt);
        Assert.Equal(created.CreatedAt, created.UpdatedAt);
        Assert.Equal(created.CreatedAt, updated.CreatedAt);
        Assert.True(updated.UpdatedAt >= created.UpdatedAt);
        Assert.NotEqual(created.Version, updated.Version);
        Assert.Equal("After", updated.Name);
    }

    [Fact]
    public async Task A_stale_writer_cannot_overwrite_another_requests_changes()
    {
        var organization = Organization.Create("Before");
        await SaveAsync(db => db.Add(organization));
        await using var first = factory.CreateScope();
        await using var second = factory.CreateScope();
        var firstCopy = await Db(first).Organizations.SingleAsync(o => o.Id == organization.Id, Ct);
        var staleCopy = await Db(second).Organizations.SingleAsync(o => o.Id == organization.Id, Ct);

        firstCopy.Rename("Saved");
        await Db(first).SaveChangesAsync(Ct);
        staleCopy.Rename("Stale");

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => Db(second).SaveChangesAsync(Ct));
        Assert.Equal("Saved", (await FindOrganizationAsync(organization.Id)).Name);
    }

    [Fact]
    public async Task Removing_an_entity_leaves_a_tombstone_that_queries_skip()
    {
        var organization = Organization.Create("Removed");
        await SaveAsync(db => db.Add(organization));
        var before = await FindOrganizationAsync(organization.Id);

        await SaveAsync(async db => db.Remove(await db.Organizations.SingleAsync(o => o.Id == organization.Id, Ct)));

        await using var scope = factory.CreateScope();
        var db = Db(scope);
        Assert.False(await db.Organizations.AnyAsync(o => o.Id == organization.Id, Ct));

        var tombstone = await db.Organizations
            .IgnoreQueryFilters([QueryFilters.SoftDelete])
            .SingleAsync(o => o.Id == organization.Id, Ct);
        Assert.NotNull(tombstone.DeletedAt);
        Assert.NotEqual(before.Version, tombstone.Version);
    }

    [Fact]
    public async Task A_user_can_only_be_a_member_once_but_can_rejoin_after_removal()
    {
        var (workspaceId, userId) = await CreateWorkspaceAndUserAsync();
        var membership = WorkspaceMember.Create(workspaceId, userId, WorkspaceRole.Member);
        await SaveAsync(db => db.Add(membership));

        await Assert.ThrowsAsync<DbUpdateException>(() =>
            SaveAsync(db => db.Add(WorkspaceMember.Create(workspaceId, userId, WorkspaceRole.Admin))));

        await SaveAsync(async db => db.Remove(await db.WorkspaceMembers.SingleAsync(m => m.Id == membership.Id, Ct)));
        await SaveAsync(db => db.Add(WorkspaceMember.Create(workspaceId, userId, WorkspaceRole.Admin)));

        await using var scope = factory.CreateScope();
        var live = await Db(scope).WorkspaceMembers.SingleAsync(m => m.WorkspaceId == workspaceId, Ct);
        Assert.Equal(WorkspaceRole.Admin, live.Role);
    }

    private static AppDbContext Db(AsyncServiceScope scope) => scope.ServiceProvider.GetRequiredService<AppDbContext>();

    private Task SaveAsync(Action<AppDbContext> change) => SaveAsync(db =>
    {
        change(db);
        return Task.CompletedTask;
    });

    // Every change runs in its own scope on freshly loaded rows, the way a request would.
    private async Task SaveAsync(Func<AppDbContext, Task> change)
    {
        await using var scope = factory.CreateScope();
        var db = Db(scope);
        await change(db);
        await db.SaveChangesAsync(Ct);
    }

    private async Task<Organization> FindOrganizationAsync(Guid id)
    {
        await using var scope = factory.CreateScope();
        return await Db(scope).Organizations.AsNoTracking().SingleAsync(o => o.Id == id, Ct);
    }

    private async Task<(Guid WorkspaceId, Guid UserId)> CreateWorkspaceAndUserAsync()
    {
        var email = $"user-{Guid.NewGuid():N}@example.com";
        var user = new AppUser
        {
            UserName = email,
            NormalizedUserName = email.ToUpperInvariant(),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            SecurityStamp = Guid.NewGuid().ToString(),
            FullName = "Test User",
            TimeZone = "UTC",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        var organization = Organization.Create("Organization");
        var workspace = Workspace.Create(organization.Id, "Workspace");

        await SaveAsync(db => db.AddRange(user, organization, workspace));
        return (workspace.Id, user.Id);
    }
}
