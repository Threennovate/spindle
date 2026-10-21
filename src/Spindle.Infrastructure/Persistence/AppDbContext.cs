using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Spindle.Domain.AgentActivity;
using Spindle.Domain.Billing;
using Spindle.Domain.Clients;
using Spindle.Domain.Groups;
using Spindle.Domain.Invitations;
using Spindle.Domain.Organizations;
using Spindle.Domain.Projects;
using Spindle.Domain.Reports;
using Spindle.Domain.Subscriptions;
using Spindle.Domain.Tags;
using Spindle.Domain.TimeEntries;
using Spindle.Domain.Workspaces;
using Spindle.Infrastructure.Identity;

namespace Spindle.Infrastructure.Persistence;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : IdentityUserContext<AppUser, Guid>(options), IDataProtectionKeyContext
{
    /// <summary>
    /// The keys that protect password reset and email confirmation links. Kept in the database, so links stay valid
    /// when the app restarts or runs on several servers.
    /// </summary>
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    public DbSet<Organization> Organizations => Set<Organization>();

    public DbSet<OrganizationMember> OrganizationMembers => Set<OrganizationMember>();

    public DbSet<OrganizationSubscription> OrganizationSubscriptions => Set<OrganizationSubscription>();

    public DbSet<SubscriptionChange> SubscriptionChanges => Set<SubscriptionChange>();

    public DbSet<ProviderEventReceipt> ProviderEventReceipts => Set<ProviderEventReceipt>();

    public DbSet<Workspace> Workspaces => Set<Workspace>();

    public DbSet<WorkspaceMember> WorkspaceMembers => Set<WorkspaceMember>();

    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    public DbSet<PasskeyChallenge> PasskeyChallenges => Set<PasskeyChallenge>();

    public DbSet<ExternalSignInCode> ExternalSignInCodes => Set<ExternalSignInCode>();

    public DbSet<PushDevice> PushDevices => Set<PushDevice>();

    public DbSet<OAuthClient> OAuthClients => Set<OAuthClient>();

    public DbSet<OAuthAuthorizationCode> OAuthAuthorizationCodes => Set<OAuthAuthorizationCode>();

    public DbSet<AgentConnection> AgentConnections => Set<AgentConnection>();

    public DbSet<Client> Clients => Set<Client>();

    public DbSet<Project> Projects => Set<Project>();

    public DbSet<ProjectTask> Tasks => Set<ProjectTask>();

    public DbSet<Tag> Tags => Set<Tag>();

    public DbSet<TimeEntry> TimeEntries => Set<TimeEntry>();

    public DbSet<Invitation> Invitations => Set<Invitation>();

    public DbSet<ProjectMember> ProjectMembers => Set<ProjectMember>();

    public DbSet<Group> Groups => Set<Group>();

    public DbSet<GroupMember> GroupMembers => Set<GroupMember>();

    public DbSet<SavedReport> SavedReports => Set<SavedReport>();

    public DbSet<SharedReport> SharedReports => Set<SharedReport>();

    public DbSet<Rate> Rates => Set<Rate>();

    public DbSet<BilledTime> BilledTimes => Set<BilledTime>();

    public DbSet<Agreement> Agreements => Set<Agreement>();

    public DbSet<AgentSession> AgentSessions => Set<AgentSession>();

    public DbSet<AgentRepositoryLink> AgentRepositoryLinks => Set<AgentRepositoryLink>();

    public DbSet<SplitSuggestionDismissal> SplitSuggestionDismissals => Set<SplitSuggestionDismissal>();

    public DbSet<IgnoredRepository> IgnoredRepositories => Set<IgnoredRepository>();

    public DbSet<AgentProposal> AgentProposals => Set<AgentProposal>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<UtcDateTimeOffsetConverter>();
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.HasPostgresExtension("citext");
        builder.Entity<AppUser>().Property(u => u.Email).HasColumnType("citext");
        builder.Entity<AppUser>().Property(u => u.UserName).HasColumnType("citext");
        builder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
    }
}
