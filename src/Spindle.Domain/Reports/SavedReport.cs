using Spindle.Domain.Common;

namespace Spindle.Domain.Reports;

public enum ReportKind
{
    Summary,
    Detailed,
    Weekly,
}

/// <summary>A report someone wants to see again: its kind, and its period, filters and grouping as the apps store them.</summary>
public sealed class SavedReport : Entity
{
    public const int NameMaxLength = 100;
    public const int ParametersMaxLength = 8000;

    private SavedReport()
    {
        Name = string.Empty;
        Parameters = string.Empty;
    }

    public Guid WorkspaceId { get; private set; }

    public Guid UserId { get; private set; }

    public string Name { get; private set; }

    public ReportKind Kind { get; private set; }

    /// <summary>A JSON object; its meaning belongs to the apps, so they can add to it without a server change.</summary>
    public string Parameters { get; private set; }

    public static SavedReport Create(Guid workspaceId, Guid userId, string name, ReportKind kind, string parameters)
    {
        var report = new SavedReport
        {
            WorkspaceId = Guard.Id(workspaceId),
            UserId = Guard.Id(userId),
            Kind = kind,
        };
        report.Update(name, parameters);
        return report;
    }

    public void Update(string name, string parameters)
    {
        Name = Guard.Name(name, NameMaxLength);
        Parameters = parameters.Length <= ParametersMaxLength
            ? parameters
            : throw new DomainValidationException("parameters", "The report's settings are too long to save.");
    }
}
