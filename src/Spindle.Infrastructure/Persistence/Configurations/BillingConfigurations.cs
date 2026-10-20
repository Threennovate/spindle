using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Spindle.Domain.Billing;
using Spindle.Domain.Projects;
using Spindle.Domain.Workspaces;
using Spindle.Infrastructure.Identity;

namespace Spindle.Infrastructure.Persistence.Configurations;

internal sealed class RateConfiguration : EntityConfiguration<Rate>
{
    protected override void ConfigureEntity(EntityTypeBuilder<Rate> builder)
    {
        builder.Property(r => r.Kind).HasConversion<string>().HasMaxLength(16);
        builder.Property(r => r.Amount).HasPrecision(18, 2);
        builder.Ignore(r => r.Scope);
        builder.Ignore(r => r.Period);

        builder.HasOne<Workspace>().WithMany().HasForeignKey(r => r.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Project>().WithMany().HasForeignKey(r => r.ProjectId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<AppUser>().WithMany().HasForeignKey(r => r.UserId).OnDelete(DeleteBehavior.Restrict);

        // One period per scope and first day. NULLS NOT DISTINCT covers the workspace's own rate and "from the start".
        builder.HasIndex(r => new { r.WorkspaceId, r.Kind, r.ProjectId, r.UserId, r.EffectiveFrom })
            .IsUnique()
            .AreNullsDistinct(false)
            .HasFilter("\"DeletedAt\" IS NULL");
        builder.HasIndex(r => r.ProjectId);
        builder.HasIndex(r => r.UserId);
    }
}

internal sealed class BilledTimeConfiguration : EntityConfiguration<BilledTime>
{
    protected override void ConfigureEntity(EntityTypeBuilder<BilledTime> builder)
    {
        builder.Property(b => b.Description).HasMaxLength(BilledTime.DescriptionMaxLength);
        builder.Ignore(b => b.IsEmpty);

        builder.HasOne<Workspace>().WithMany().HasForeignKey(b => b.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Project>().WithMany().HasForeignKey(b => b.ProjectId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<AppUser>().WithMany().HasForeignKey(b => b.UserId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(b => new { b.ProjectId, b.UserId, b.Day }).IsUnique().HasFilter("\"DeletedAt\" IS NULL");
        // Reports read a workspace's billed time by day.
        builder.HasIndex(b => new { b.WorkspaceId, b.Day });
        builder.HasIndex(b => b.UserId);
    }
}

internal sealed class AgreementConfiguration : EntityConfiguration<Agreement>
{
    protected override void ConfigureEntity(EntityTypeBuilder<Agreement> builder)
    {
        builder.Property(a => a.Name).HasColumnType("citext").HasMaxLength(Agreement.NameMaxLength);
        builder.Property(a => a.Kind).HasConversion<string>().HasMaxLength(16);
        builder.Property(a => a.Period).HasConversion<string>().HasMaxLength(16);
        builder.Property(a => a.Amount).HasPrecision(18, 2);
        builder.Property(a => a.Hours).HasPrecision(9, 2);
        builder.Ignore(a => a.IncludedSeconds);

        builder.HasOne<Workspace>().WithMany().HasForeignKey(a => a.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Project>().WithMany().HasForeignKey(a => a.ProjectId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(a => new { a.ProjectId, a.Name }).IsUnique().HasFilter("\"DeletedAt\" IS NULL");
        builder.HasIndex(a => a.WorkspaceId);
    }
}
