using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Spindle.Domain.AgentActivity;
using Spindle.Domain.Projects;
using Spindle.Domain.TimeEntries;
using Spindle.Domain.Workspaces;
using Spindle.Infrastructure.Identity;

namespace Spindle.Infrastructure.Persistence.Configurations;

// Agent activity is the person's own, not a workspace's: plain rows under the user, replaced whole and deleted for
// good, so none of it lingers as a tombstone once sync is turned off.

internal sealed class AgentSessionConfiguration : IEntityTypeConfiguration<AgentSession>
{
    public void Configure(EntityTypeBuilder<AgentSession> builder)
    {
        builder.Property(s => s.Id).ValueGeneratedNever();
        builder.Property(s => s.Source).HasMaxLength(AgentSession.SourceMaxLength);
        builder.Property(s => s.SessionId).HasMaxLength(AgentSession.SessionIdMaxLength);
        builder.Property(s => s.ParentSessionId).HasMaxLength(AgentSession.SessionIdMaxLength);
        builder.Property(s => s.RepositoryIdentity).HasMaxLength(AgentRepository.IdentityMaxLength);
        builder.Property(s => s.RepositoryName).HasMaxLength(AgentRepository.NameMaxLength);
        builder.Property(s => s.Title).HasMaxLength(AgentSession.TitleMaxLength);
        builder.PrimitiveCollection(s => s.Branches);
        builder.PrimitiveCollection(s => s.Touches);
        builder.PrimitiveCollection(s => s.RunStarts);
        builder.PrimitiveCollection(s => s.RunEnds);
        builder.Ignore(s => s.Repository);

        builder.HasOne<AppUser>().WithMany().HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(s => new { s.UserId, s.DeviceId, s.Source, s.SessionId }).IsUnique();
        // Everything overlapping an entry.
        builder.HasIndex(s => new { s.UserId, s.FirstAt, s.LastAt });
    }
}

internal sealed class AgentRepositoryLinkConfiguration : IEntityTypeConfiguration<AgentRepositoryLink>
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public void Configure(EntityTypeBuilder<AgentRepositoryLink> builder)
    {
        builder.HasKey(l => new { l.UserId, l.Identity });
        builder.Property(l => l.Identity).HasMaxLength(AgentRepository.IdentityMaxLength);
        builder.Property(l => l.Name).HasMaxLength(AgentRepository.NameMaxLength);
        builder.Property(l => l.BranchRules).HasColumnType("jsonb").HasConversion(
            rules => JsonSerializer.Serialize(rules, Options),
            json => JsonSerializer.Deserialize<List<BranchRule>>(json, Options) ?? new List<BranchRule>(),
            new ValueComparer<List<BranchRule>>((a, b) => a!.SequenceEqual(b!), rules => rules.Count, rules => rules.ToList()));

        builder.HasOne<AppUser>().WithMany().HasForeignKey(l => l.UserId).OnDelete(DeleteBehavior.Restrict);
        // A project erased with its workspace takes the links to it along.
        builder.HasOne<Project>().WithMany().HasForeignKey(l => l.ProjectId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class SplitSuggestionDismissalConfiguration : IEntityTypeConfiguration<SplitSuggestionDismissal>
{
    public void Configure(EntityTypeBuilder<SplitSuggestionDismissal> builder)
    {
        builder.HasKey(d => new { d.UserId, d.EntryId });

        builder.HasOne<AppUser>().WithMany().HasForeignKey(d => d.UserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<TimeEntry>().WithMany().HasForeignKey(d => d.EntryId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class IgnoredRepositoryConfiguration : IEntityTypeConfiguration<IgnoredRepository>
{
    public void Configure(EntityTypeBuilder<IgnoredRepository> builder)
    {
        builder.HasKey(i => new { i.UserId, i.Hash });
        builder.Property(i => i.Hash).HasMaxLength(IgnoredRepository.HashLength);

        builder.HasOne<AppUser>().WithMany().HasForeignKey(i => i.UserId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class AgentProposalConfiguration : IEntityTypeConfiguration<AgentProposal>
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public void Configure(EntityTypeBuilder<AgentProposal> builder)
    {
        builder.Property(p => p.Id).ValueGeneratedNever();
        builder.Property(p => p.Status).HasConversion<string>().HasMaxLength(16);
        builder.Property(p => p.Reason).HasMaxLength(AgentProposal.ReasonMaxLength);
        builder.Property(p => p.Client).HasMaxLength(AgentProposal.ClientMaxLength);
        builder.Property(p => p.Fields).HasColumnType("jsonb").HasConversion(
            fields => JsonSerializer.Serialize(fields, Options),
            json => JsonSerializer.Deserialize<ProposedFields>(json, Options) ?? new ProposedFields(null, null, null, null),
            new ValueComparer<ProposedFields>((a, b) => JsonSerializer.Serialize(a, Options) == JsonSerializer.Serialize(b, Options),
                fields => JsonSerializer.Serialize(fields, Options).GetHashCode(StringComparison.Ordinal), fields => fields));
        builder.Property(p => p.Original).HasColumnType("jsonb").HasConversion(
            original => original == null ? null : JsonSerializer.Serialize(original, Options),
            json => json == null ? null : JsonSerializer.Deserialize<ProposedEntrySnapshot>(json, Options),
            new ValueComparer<ProposedEntrySnapshot?>((a, b) => JsonSerializer.Serialize(a, Options) == JsonSerializer.Serialize(b, Options),
                original => JsonSerializer.Serialize(original, Options).GetHashCode(StringComparison.Ordinal), original => original));
        builder.Ignore(p => p.IsNew);

        builder.HasOne<AppUser>().WithMany().HasForeignKey(p => p.UserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Workspace>().WithMany().HasForeignKey(p => p.WorkspaceId).OnDelete(DeleteBehavior.Cascade);

        // What waits, and what became of the rest lately, in a workspace.
        builder.HasIndex(p => new { p.UserId, p.WorkspaceId, p.ProposedAt });
    }
}
