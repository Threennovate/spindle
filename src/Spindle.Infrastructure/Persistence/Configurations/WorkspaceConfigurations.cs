using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Spindle.Domain.Organizations;
using Spindle.Domain.Workspaces;
using Spindle.Infrastructure.Identity;

namespace Spindle.Infrastructure.Persistence.Configurations;

internal sealed class WorkspaceConfiguration : EntityConfiguration<Workspace>
{
    protected override void ConfigureEntity(EntityTypeBuilder<Workspace> builder)
    {
        builder.Property(w => w.Name).HasColumnType("citext").HasMaxLength(Workspace.NameMaxLength);
        builder.Property(w => w.Currency).HasMaxLength(3).IsFixedLength().IsUnicode(false).HasDefaultValue("EUR");

        builder.HasOne<Organization>().WithMany().HasForeignKey(w => w.OrganizationId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class WorkspaceMemberConfiguration : EntityConfiguration<WorkspaceMember>
{
    protected override void ConfigureEntity(EntityTypeBuilder<WorkspaceMember> builder)
    {
        builder.Property(m => m.Role).HasConversion<string>().HasMaxLength(20);

        builder.HasOne<Workspace>().WithMany().HasForeignKey(m => m.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<AppUser>().WithMany().HasForeignKey(m => m.UserId).OnDelete(DeleteBehavior.Restrict);

        // A removed member stays as a tombstone, so uniqueness only applies to live rows.
        builder.HasIndex(m => new { m.WorkspaceId, m.UserId }).IsUnique().HasFilter("\"DeletedAt\" IS NULL");
        builder.HasIndex(m => m.UserId);
    }
}
