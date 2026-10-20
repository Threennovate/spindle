using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Spindle.Domain.Workspaces;
using Spindle.Infrastructure.Identity;

namespace Spindle.Infrastructure.Persistence.Configurations;

// Map the Identity passkey entity explicitly, preserving the existing Identity v1 table definitions.
internal sealed class PasskeyConfiguration : IEntityTypeConfiguration<IdentityUserPasskey<Guid>>
{
    public void Configure(EntityTypeBuilder<IdentityUserPasskey<Guid>> builder)
    {
        builder.ToTable("AspNetUserPasskeys");
        builder.HasKey(p => p.CredentialId);
        builder.Property(p => p.CredentialId).HasMaxLength(1024);
        builder.OwnsOne(p => p.Data).ToJson();
        builder.HasOne<AppUser>().WithMany().HasForeignKey(p => p.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class PasskeyChallengeConfiguration : IEntityTypeConfiguration<PasskeyChallenge>
{
    public void Configure(EntityTypeBuilder<PasskeyChallenge> builder)
    {
        builder.HasKey(c => c.TokenHash);
        builder.Property(c => c.TokenHash).HasMaxLength(64).IsFixedLength();
        builder.Property(c => c.Operation).HasMaxLength(16);
        builder.HasIndex(c => c.ExpiresAt);
        builder.HasOne<AppUser>().WithMany().HasForeignKey(c => c.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class ExternalSignInCodeConfiguration : IEntityTypeConfiguration<ExternalSignInCode>
{
    public void Configure(EntityTypeBuilder<ExternalSignInCode> builder)
    {
        builder.HasKey(c => c.TokenHash);
        builder.Property(c => c.TokenHash).HasMaxLength(64).IsFixedLength();
        builder.Property(c => c.BindingHash).HasMaxLength(64);
        builder.Property(c => c.Error).HasMaxLength(ExternalSignInCode.ErrorMaxLength);
        builder.Property(c => c.ReturnPath).HasMaxLength(ExternalSignInCode.ReturnPathMaxLength);
        builder.HasIndex(c => c.ExpiresAt);
        builder.HasOne<AppUser>().WithMany().HasForeignKey(c => c.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class AppUserConfiguration : IEntityTypeConfiguration<AppUser>
{
    public void Configure(EntityTypeBuilder<AppUser> builder)
    {
        builder.Property(u => u.FullName).HasMaxLength(AppUser.FullNameMaxLength);
        builder.Property(u => u.TimeZone).HasMaxLength(AppUser.TimeZoneMaxLength);
        builder.Property(u => u.TermsVersion).HasMaxLength(AppUser.TermsVersionMaxLength);

        // Defaults live in the class; the migration that added these gave existing accounts the same values.
        builder.Property(u => u.DateFormat).HasMaxLength(16);
        builder.Property(u => u.TimeFormat).HasMaxLength(8);
        builder.Property(u => u.DurationFormat).HasMaxLength(8);

        builder.HasOne<Workspace>().WithMany().HasForeignKey(u => u.DefaultWorkspaceId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> builder)
    {
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).ValueGeneratedNever();
        builder.Property(t => t.TokenHash).HasMaxLength(RefreshToken.TokenHashLength).IsFixedLength();
        builder.Property(t => t.DeviceName).HasMaxLength(RefreshToken.DeviceNameMaxLength);
        builder.Property(t => t.Version).IsRowVersion();

        builder.HasOne<AppUser>().WithMany().HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(t => t.TokenHash).IsUnique();
        builder.HasIndex(t => t.FamilyId);
        builder.HasIndex(t => t.UserId);
    }
}

internal sealed class PushDeviceConfiguration : IEntityTypeConfiguration<PushDevice>
{
    public void Configure(EntityTypeBuilder<PushDevice> builder)
    {
        builder.HasKey(d => d.SessionId);
        builder.Property(d => d.SessionId).ValueGeneratedNever();
        builder.Property(d => d.LiveActivityToken).HasMaxLength(PushDevice.TokenMaxLength);
        builder.Property(d => d.LastSent).HasMaxLength(64);

        builder.HasOne<AppUser>().WithMany().HasForeignKey(d => d.UserId).OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(d => d.UserId);
    }
}

internal sealed class OAuthClientConfiguration : IEntityTypeConfiguration<OAuthClient>
{
    public void Configure(EntityTypeBuilder<OAuthClient> builder)
    {
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).HasMaxLength(OAuthClient.IdLength);
        builder.Property(c => c.Name).HasMaxLength(OAuthClient.NameMaxLength);
        builder.Property(c => c.SecretHash).HasMaxLength(32).IsFixedLength();
    }
}

internal sealed class OAuthAuthorizationCodeConfiguration : IEntityTypeConfiguration<OAuthAuthorizationCode>
{
    public void Configure(EntityTypeBuilder<OAuthAuthorizationCode> builder)
    {
        builder.HasKey(c => c.CodeHash);
        builder.Property(c => c.CodeHash).HasMaxLength(32).IsFixedLength();
        builder.Property(c => c.ClientId).HasMaxLength(OAuthClient.RedirectUriMaxLength);
        builder.Property(c => c.ClientName).HasMaxLength(OAuthClient.NameMaxLength);
        builder.Property(c => c.RedirectUri).HasMaxLength(OAuthClient.RedirectUriMaxLength);
        builder.Property(c => c.CodeChallenge).HasMaxLength(128);
        builder.Property(c => c.Scope).HasMaxLength(100);
        builder.Property(c => c.Resource).HasMaxLength(OAuthClient.RedirectUriMaxLength);
        builder.HasIndex(c => c.ExpiresAt);
        builder.HasOne<AppUser>().WithMany().HasForeignKey(c => c.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Workspace>().WithMany().HasForeignKey(c => c.WorkspaceId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class AgentConnectionConfiguration : IEntityTypeConfiguration<AgentConnection>
{
    public void Configure(EntityTypeBuilder<AgentConnection> builder)
    {
        builder.HasKey(c => c.SessionId);
        builder.Property(c => c.SessionId).ValueGeneratedNever();
        builder.Property(c => c.ClientId).HasMaxLength(OAuthClient.RedirectUriMaxLength);
        builder.Property(c => c.Scope).HasMaxLength(100);
        builder.Property(c => c.Resource).HasMaxLength(OAuthClient.RedirectUriMaxLength);
        builder.HasIndex(c => c.UserId);
        builder.HasOne<AppUser>().WithMany().HasForeignKey(c => c.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Workspace>().WithMany().HasForeignKey(c => c.WorkspaceId).OnDelete(DeleteBehavior.Cascade);
    }
}
