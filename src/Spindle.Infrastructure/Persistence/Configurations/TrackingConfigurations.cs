using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Spindle.Domain.Billing;
using Spindle.Domain.Clients;
using Spindle.Domain.Groups;
using Spindle.Domain.Projects;
using Spindle.Domain.Reports;
using Spindle.Domain.Tags;
using Spindle.Domain.TimeEntries;
using Spindle.Domain.Workspaces;
using Spindle.Infrastructure.Identity;

namespace Spindle.Infrastructure.Persistence.Configurations;

// Names are unique among live rows only: a deleted client stays as a tombstone and must not block its name.
// citext keeps names case-insensitive: "Acme" and "acme" count as the same name.

internal sealed class ClientConfiguration : EntityConfiguration<Client>
{
    protected override void ConfigureEntity(EntityTypeBuilder<Client> builder)
    {
        builder.Property(c => c.Name).HasColumnType("citext").HasMaxLength(Client.NameMaxLength);

        builder.HasOne<Workspace>().WithMany().HasForeignKey(c => c.WorkspaceId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(c => new { c.WorkspaceId, c.Name }).IsUnique().HasFilter("\"DeletedAt\" IS NULL");
    }
}

internal sealed class ProjectConfiguration : EntityConfiguration<Project>
{
    protected override void ConfigureEntity(EntityTypeBuilder<Project> builder)
    {
        builder.Property(p => p.Name).HasColumnType("citext").HasMaxLength(Project.NameMaxLength);
        builder.Property(p => p.Color).HasMaxLength(7).IsFixedLength().IsUnicode(false);
        builder.Property(p => p.Estimate).HasConversion(DurationConverters.Seconds).HasColumnName("EstimateSeconds");
        builder.Property(p => p.BillingMode).HasConversion<string>().HasMaxLength(16);
        builder.Ignore(p => p.SeparateBilledTime);

        builder.HasOne<Workspace>().WithMany().HasForeignKey(p => p.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Client>().WithMany().HasForeignKey(p => p.ClientId).OnDelete(DeleteBehavior.Restrict);

        // NULLS NOT DISTINCT also enforces unique names for projects without a client.
        builder.HasIndex(p => new { p.WorkspaceId, p.ClientId, p.Name }).IsUnique().AreNullsDistinct(false).HasFilter("\"DeletedAt\" IS NULL");
    }
}

internal sealed class ProjectMemberConfiguration : EntityConfiguration<ProjectMember>
{
    protected override void ConfigureEntity(EntityTypeBuilder<ProjectMember> builder)
    {
        builder.HasOne<Workspace>().WithMany().HasForeignKey(m => m.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Project>().WithMany().HasForeignKey(m => m.ProjectId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<AppUser>().WithMany().HasForeignKey(m => m.UserId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(m => new { m.ProjectId, m.UserId }).IsUnique().HasFilter("\"DeletedAt\" IS NULL");
        builder.HasIndex(m => m.UserId);
    }
}

internal sealed class GroupConfiguration : EntityConfiguration<Group>
{
    protected override void ConfigureEntity(EntityTypeBuilder<Group> builder)
    {
        builder.Property(g => g.Name).HasColumnType("citext").HasMaxLength(Group.NameMaxLength);

        builder.HasOne<Workspace>().WithMany().HasForeignKey(g => g.WorkspaceId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(g => new { g.WorkspaceId, g.Name }).IsUnique().HasFilter("\"DeletedAt\" IS NULL");
    }
}

internal sealed class GroupMemberConfiguration : EntityConfiguration<GroupMember>
{
    protected override void ConfigureEntity(EntityTypeBuilder<GroupMember> builder)
    {
        builder.HasOne<Group>().WithMany().HasForeignKey(m => m.GroupId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<AppUser>().WithMany().HasForeignKey(m => m.UserId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(m => new { m.GroupId, m.UserId }).IsUnique().HasFilter("\"DeletedAt\" IS NULL");
    }
}

internal sealed class ProjectTaskConfiguration : EntityConfiguration<ProjectTask>
{
    protected override void ConfigureEntity(EntityTypeBuilder<ProjectTask> builder)
    {
        builder.ToTable("Tasks");
        builder.Property(t => t.Name).HasColumnType("citext").HasMaxLength(ProjectTask.NameMaxLength);
        builder.Property(t => t.Estimate).HasConversion(DurationConverters.Seconds).HasColumnName("EstimateSeconds");

        builder.HasOne<Workspace>().WithMany().HasForeignKey(t => t.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Project>().WithMany().HasForeignKey(t => t.ProjectId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Agreement>().WithMany().HasForeignKey(t => t.AgreementId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(t => new { t.ProjectId, t.Name }).IsUnique().HasFilter("\"DeletedAt\" IS NULL");
        builder.HasIndex(t => t.AgreementId);
    }
}

internal sealed class TagConfiguration : EntityConfiguration<Tag>
{
    protected override void ConfigureEntity(EntityTypeBuilder<Tag> builder)
    {
        builder.Property(t => t.Name).HasColumnType("citext").HasMaxLength(Tag.NameMaxLength);

        builder.HasOne<Workspace>().WithMany().HasForeignKey(t => t.WorkspaceId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(t => new { t.WorkspaceId, t.Name }).IsUnique().HasFilter("\"DeletedAt\" IS NULL");
    }
}

internal sealed class TimeEntryConfiguration : EntityConfiguration<TimeEntry>
{
    protected override void ConfigureEntity(EntityTypeBuilder<TimeEntry> builder)
    {
        builder.ToTable(table =>
        {
            table.HasCheckConstraint("CK_TimeEntries_StopNotBeforeStart", "\"Stop\" IS NULL OR \"Stop\" >= \"Start\"");
            table.HasCheckConstraint("CK_TimeEntries_SplitHasShares", "\"IsSplit\" = (jsonb_array_length(\"Shares\") > 1)");
        });

        builder.Property(e => e.Description).HasMaxLength(TimeEntry.DescriptionMaxLength);
        builder.PrimitiveCollection(e => e.TagIds);
        builder.Ignore(e => e.Parts);

        // In the entry's own row, like its tags, so changing the split changes the entry's version.
        builder.Property(e => e.Shares)
            .HasColumnType("jsonb")
            .HasConversion(TimeShareConversion.Converter, TimeShareConversion.Comparer);

        builder.HasOne<Workspace>().WithMany().HasForeignKey(e => e.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<AppUser>().WithMany().HasForeignKey(e => e.UserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Project>().WithMany().HasForeignKey(e => e.ProjectId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ProjectTask>().WithMany().HasForeignKey(e => e.TaskId).OnDelete(DeleteBehavior.Restrict);

        // One running timer per user, across all workspaces.
        builder.HasIndex(e => e.UserId)
            .IsUnique()
            .HasFilter("\"Stop\" IS NULL AND \"DeletedAt\" IS NULL")
            .HasDatabaseName("UX_TimeEntries_OneRunningPerUser");

        // The timer page lists a user's entries by start; reports scan a workspace by start.
        builder.HasIndex(e => new { e.UserId, e.WorkspaceId, e.Start });
        builder.HasIndex(e => new { e.WorkspaceId, e.Start });
        builder.HasIndex(e => e.ProjectId);
        builder.HasIndex(e => e.TaskId);
    }
}

internal static class TimeShareConversion
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static readonly ValueConverter<TimeShare[], string> Converter = new(
        shares => JsonSerializer.Serialize(shares, Options),
        json => JsonSerializer.Deserialize<TimeShare[]>(json, Options) ?? Array.Empty<TimeShare>());

    public static readonly ValueComparer<TimeShare[]> Comparer = new(
        (a, b) => a!.SequenceEqual(b!),
        shares => shares.Aggregate(0, (hash, share) => HashCode.Combine(hash, share)),
        shares => shares.ToArray());
}

internal static class DurationConverters
{
    /// <summary>Durations of any length, stored as whole seconds (SQL Server's <c>time</c> type stops at 24 hours).</summary>
    public static readonly ValueConverter<TimeSpan, long> Seconds =
        new(value => (long)value.TotalSeconds, seconds => TimeSpan.FromSeconds(seconds));
}

internal sealed class SavedReportConfiguration : EntityConfiguration<SavedReport>
{
    protected override void ConfigureEntity(EntityTypeBuilder<SavedReport> builder)
    {
        builder.Property(r => r.Name).HasColumnType("citext").HasMaxLength(SavedReport.NameMaxLength);
        builder.Property(r => r.Kind).HasConversion<string>().HasMaxLength(16);
        builder.Property(r => r.Parameters).HasMaxLength(SavedReport.ParametersMaxLength);

        builder.HasOne<Workspace>().WithMany().HasForeignKey(r => r.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<AppUser>().WithMany().HasForeignKey(r => r.UserId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(r => new { r.WorkspaceId, r.UserId, r.Name }).IsUnique().HasFilter("\"DeletedAt\" IS NULL");
    }
}

internal sealed class SharedReportConfiguration : EntityConfiguration<SharedReport>
{
    protected override void ConfigureEntity(EntityTypeBuilder<SharedReport> builder)
    {
        builder.Property(r => r.Title).HasMaxLength(SharedReport.TitleMaxLength);
        builder.Property(r => r.Token).HasMaxLength(64).IsUnicode(false);
        builder.Property(r => r.Period).HasConversion<string>().HasMaxLength(16);
        builder.Property(r => r.TimeZone).HasMaxLength(AppUser.TimeZoneMaxLength);
        builder.Property(r => r.WeekStart).HasConversion<int>();
        builder.PrimitiveCollection(r => r.UserIds);
        builder.PrimitiveCollection(r => r.GroupIds);
        builder.PrimitiveCollection(r => r.ProjectIds);
        builder.PrimitiveCollection(r => r.ClientIds);
        builder.PrimitiveCollection(r => r.TaskIds);
        builder.PrimitiveCollection(r => r.TagIds);
        builder.Property(r => r.Description).HasMaxLength(200);
        builder.Property(r => r.Rounding).HasConversion<string>().HasMaxLength(16);
        builder.Property(r => r.HourFormat).HasConversion<string>().HasMaxLength(16);
        builder.Property(r => r.Sender).HasConversion<string>().HasMaxLength(16);
        builder.Property(r => r.SenderName).HasMaxLength(SharedReport.SenderNameMaxLength);
        builder.Ignore(r => r.Filter);
        builder.Ignore(r => r.IsRelative);

        builder.HasOne<Workspace>().WithMany().HasForeignKey(r => r.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<AppUser>().WithMany().HasForeignKey(r => r.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);

        // A removed report's token stays taken, so an old link can never open a new report.
        builder.HasIndex(r => r.Token).IsUnique();
        builder.HasIndex(r => r.WorkspaceId);
        builder.HasIndex(r => r.CreatedByUserId);
    }
}
