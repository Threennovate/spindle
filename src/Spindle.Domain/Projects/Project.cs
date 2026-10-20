using Spindle.Domain.Common;

namespace Spindle.Domain.Projects;

public sealed class Project : Entity
{
    public const int NameMaxLength = 100;

    private Project()
    {
        Name = string.Empty;
        Color = string.Empty;
    }

    public Guid WorkspaceId { get; private set; }

    public Guid? ClientId { get; private set; }

    public string Name { get; private set; }

    /// <summary>Hex color such as <c>#6D28D9</c>, shown next to the project everywhere.</summary>
    public string Color { get; private set; }

    /// <summary>Private projects are visible only to their members and workspace admins.</summary>
    public bool IsPrivate { get; private set; }

    /// <summary>Whether new time entries on this project are billable by default.</summary>
    public bool IsBillable { get; private set; }

    /// <summary>How the time billed to the client is worked out: as tracked, or entered day by day.</summary>
    public BillingMode BillingMode { get; private set; }

    /// <summary>Whether billed time is entered day by day, rather than simply being the billable time tracked.</summary>
    public bool SeparateBilledTime => BillingMode != BillingMode.AsTracked;

    /// <summary>How long the project is expected to take in total.</summary>
    public TimeSpan? Estimate { get; private set; }

    /// <summary>Archived projects keep their time entries but cannot be picked for new ones.</summary>
    public DateTimeOffset? ArchivedAt { get; private set; }

    public static Project Create(Guid workspaceId, ProjectDetails details)
    {
        var project = new Project { WorkspaceId = Guard.Id(workspaceId) };
        project.Update(details);
        return project;
    }

    public void Update(ProjectDetails details)
    {
        Name = Guard.Name(details.Name, NameMaxLength, "name");
        ClientId = details.ClientId;
        Color = Guard.Color(details.Color, "color");
        IsPrivate = details.IsPrivate;
        IsBillable = details.IsBillable;
        Estimate = Guard.Estimate(details.Estimate, "estimateSeconds");
        BillingMode = details.BillingMode;
    }

    public void Archive(DateTimeOffset now) => ArchivedAt ??= now;

    public void Restore() => ArchivedAt = null;

    public void RemoveClient() => ClientId = null;
}

public sealed record ProjectDetails(
    string Name,
    Guid? ClientId,
    string Color,
    bool IsPrivate,
    bool IsBillable,
    TimeSpan? Estimate,
    BillingMode BillingMode = BillingMode.AsTracked);

/// <summary>How a project's billed time is worked out. Days entered by hand always count as entered.</summary>
public enum BillingMode
{
    /// <summary>The billable time tracked is billed.</summary>
    AsTracked,

    /// <summary>Billed time is entered day by day; days left empty are billed as tracked.</summary>
    PerDay,

    /// <summary>
    /// Each working day, Monday to Friday, on which someone tracked time on the project is billed as a full day of the
    /// project's daily hours. Weekends are billed as tracked. Days entered by hand count as entered.
    /// </summary>
    FullDay,
}
