using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Spindle.Domain.Organizations;
using Spindle.Infrastructure.Identity;

namespace Spindle.Infrastructure.Persistence.Configurations;

internal sealed class OrganizationConfiguration : EntityConfiguration<Organization>
{
    protected override void ConfigureEntity(EntityTypeBuilder<Organization> builder)
    {
        builder.Property(o => o.Name).HasColumnType("citext").HasMaxLength(Organization.NameMaxLength);
    }
}

internal sealed class OrganizationMemberConfiguration : EntityConfiguration<OrganizationMember>
{
    protected override void ConfigureEntity(EntityTypeBuilder<OrganizationMember> builder)
    {
        builder.Property(m => m.Role).HasConversion<string>().HasMaxLength(20);

        builder.HasOne<Organization>().WithMany().HasForeignKey(m => m.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<AppUser>().WithMany().HasForeignKey(m => m.UserId).OnDelete(DeleteBehavior.Restrict);

        // A removed member stays as a tombstone, so uniqueness only applies to live rows.
        builder.HasIndex(m => new { m.OrganizationId, m.UserId }).IsUnique().HasFilter("\"DeletedAt\" IS NULL");
        builder.HasIndex(m => m.UserId);
    }
}
