using Spindle.Domain.Common;

namespace Spindle.Domain.Projects;

/// <summary>A piece of work within a project that time can be tracked against.</summary>
public sealed class ProjectTask : Entity
{
    public const int NameMaxLength = 100;

    private ProjectTask()
    {
        Name = string.Empty;
    }

    public Guid WorkspaceId { get; private set; }

    public Guid ProjectId { get; private set; }

    public string Name { get; private set; }

    public TimeSpan? Estimate { get; private set; }

    /// <summary>Done tasks keep their time entries but cannot be picked for new ones.</summary>
    public bool IsDone { get; private set; }

    /// <summary>The agreement with the client that time on the task counts towards, if any.</summary>
    public Guid? AgreementId { get; private set; }

    public static ProjectTask Create(Guid workspaceId, Guid projectId, string name, TimeSpan? estimate) => new()
    {
        WorkspaceId = Guard.Id(workspaceId),
        ProjectId = Guard.Id(projectId),
        Name = Guard.Name(name, NameMaxLength),
        Estimate = Guard.Estimate(estimate, "estimateSeconds"),
    };

    public void Update(string name, TimeSpan? estimate, bool isDone)
    {
        Name = Guard.Name(name, NameMaxLength);
        Estimate = Guard.Estimate(estimate, "estimateSeconds");
        IsDone = isDone;
    }

    public void AssignTo(Guid? agreementId) => AgreementId = agreementId;

    /// <summary>Closes or reopens the task with its agreement.</summary>
    public void MarkDone(bool isDone) => IsDone = isDone;
}
