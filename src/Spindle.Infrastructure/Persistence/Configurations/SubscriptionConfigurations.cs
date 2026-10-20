using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Spindle.Domain.Organizations;
using Spindle.Domain.Subscriptions;

namespace Spindle.Infrastructure.Persistence.Configurations;

// A subscription is the hosted service's business, not data the apps sync: a plain row per organization, which goes
// when the organization is erased.
internal sealed class OrganizationSubscriptionConfiguration : IEntityTypeConfiguration<OrganizationSubscription>
{
    public void Configure(EntityTypeBuilder<OrganizationSubscription> builder)
    {
        builder.HasKey(s => s.OrganizationId);
        builder.Property(s => s.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(s => s.TrialReminderSent).HasConversion<string>().HasMaxLength(20);
        builder.Property(s => s.Provider).HasMaxLength(ProviderEventReceipt.ProviderMaxLength);
        builder.Property(s => s.ProviderCustomerId).HasMaxLength(100);
        builder.Property(s => s.ProviderSubscriptionId).HasMaxLength(100);
        builder.Property(s => s.ProviderStatus).HasConversion<string>().HasMaxLength(20);
        builder.Property(s => s.Interval).HasConversion<string>().HasMaxLength(10);
        builder.Property(s => s.PriceCohort).HasMaxLength(OrganizationSubscription.PriceCohortMaxLength);
        builder.HasIndex(s => s.ProviderSubscriptionId);

        builder.HasOne<Organization>().WithOne().HasForeignKey<OrganizationSubscription>(s => s.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class SubscriptionChangeConfiguration : IEntityTypeConfiguration<SubscriptionChange>
{
    public void Configure(EntityTypeBuilder<SubscriptionChange> builder)
    {
        builder.Property(c => c.Id).ValueGeneratedNever();
        builder.Property(c => c.OperatorEmail).HasMaxLength(SubscriptionChange.EmailMaxLength);
        builder.Property(c => c.Kind).HasConversion<string>().HasMaxLength(20);
        builder.Property(c => c.PreviousStatus).HasConversion<string>().HasMaxLength(20);

        builder.HasOne<Organization>().WithMany().HasForeignKey(c => c.OrganizationId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(c => new { c.OrganizationId, c.At });
    }
}

internal sealed class ProviderEventReceiptConfiguration : IEntityTypeConfiguration<ProviderEventReceipt>
{
    public void Configure(EntityTypeBuilder<ProviderEventReceipt> builder)
    {
        builder.HasKey(r => new { r.Provider, r.EventId });
        builder.Property(r => r.Provider).HasMaxLength(ProviderEventReceipt.ProviderMaxLength);
        builder.Property(r => r.EventId).HasMaxLength(ProviderEventReceipt.EventIdMaxLength);
    }
}
