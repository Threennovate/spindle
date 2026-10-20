using System.Text.RegularExpressions;
using Spindle.Domain.Common;

namespace Spindle.Domain.Workspaces;

/// <summary>
/// Holds the tracked data: clients, projects, tags and time entries all belong to one workspace, and follow its
/// settings for money, permissions and which details every entry needs. Its rates are kept as
/// <see cref="Billing.Rate"/> periods.
/// </summary>
public sealed partial class Workspace : Entity
{
    public const int NameMaxLength = 140;

    private Workspace()
    {
        Name = string.Empty;
        Currency = "EUR";
    }

    public Guid OrganizationId { get; private set; }

    public string Name { get; private set; }

    /// <summary>ISO 4217 code of the currency rates and amounts are in, e.g. <c>EUR</c>.</summary>
    public string Currency { get; private set; }

    /// <summary>When set, only admins create projects and clients.</summary>
    public bool OnlyAdminsCreateProjects { get; private set; }

    /// <summary>When set, only admins create tags.</summary>
    public bool OnlyAdminsCreateTags { get; private set; }

    /// <summary>Members cannot add, change or delete time on days before this date (their own calendar day).</summary>
    public DateOnly? LockedBefore { get; private set; }

    public bool RequireDescription { get; private set; }

    public bool RequireProject { get; private set; }

    /// <summary>A task is required on projects that have open tasks.</summary>
    public bool RequireTask { get; private set; }

    public bool RequireTag { get; private set; }

    public static Workspace Create(Guid organizationId, string name) => new()
    {
        OrganizationId = Guard.Id(organizationId),
        Name = Guard.Name(name, NameMaxLength),
    };

    public void Rename(string name) => Name = Guard.Name(name, NameMaxLength);

    public void UpdateSettings(WorkspaceSettings settings)
    {
        if (!CurrencyCode().IsMatch(settings.Currency))
        {
            throw new DomainValidationException("currency", "Use a three-letter currency code such as EUR or USD.");
        }

        Name = Guard.Name(settings.Name, NameMaxLength, "name");
        Currency = settings.Currency;
        OnlyAdminsCreateProjects = settings.OnlyAdminsCreateProjects;
        OnlyAdminsCreateTags = settings.OnlyAdminsCreateTags;
        LockedBefore = settings.LockedBefore;
        RequireDescription = settings.RequireDescription;
        RequireProject = settings.RequireProject;
        RequireTask = settings.RequireTask;
        RequireTag = settings.RequireTag;
    }

    [GeneratedRegex("^[A-Z]{3}$")]
    private static partial Regex CurrencyCode();
}

public sealed record WorkspaceSettings(
    string Name,
    string Currency,
    bool OnlyAdminsCreateProjects,
    bool OnlyAdminsCreateTags,
    DateOnly? LockedBefore,
    bool RequireDescription,
    bool RequireProject,
    bool RequireTask,
    bool RequireTag);
