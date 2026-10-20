using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Spindle.Domain.Invitations;
using Spindle.Domain.Organizations;
using Spindle.Domain.Workspaces;
using Spindle.Infrastructure.Identity;

namespace Spindle.Infrastructure.Persistence.Configurations;

internal sealed class InvitationConfiguration : EntityConfiguration<Invitation>
{
    protected override void ConfigureEntity(EntityTypeBuilder<Invitation> builder)
    {
        builder.Property(i => i.Email).HasColumnType("citext").HasMaxLength(Invitation.EmailMaxLength);
        builder.Property(i => i.Role).HasConversion<string>().HasMaxLength(20);
        builder.Property(i => i.TokenHash).HasMaxLength(32).IsFixedLength();

        builder.HasOne<Organization>().WithMany().HasForeignKey(i => i.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Workspace>().WithMany().HasForeignKey(i => i.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<AppUser>().WithMany().HasForeignKey(i => i.InvitedByUserId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(i => i.TokenHash).IsUnique();

        // One open invitation per address and workspace; inviting again renews it.
        builder.HasIndex(i => new { i.WorkspaceId, i.Email })
            .IsUnique()
            .HasFilter("\"AcceptedAt\" IS NULL AND \"RevokedAt\" IS NULL AND \"DeletedAt\" IS NULL");
    }
}
