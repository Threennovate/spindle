using Spindle.Core;

namespace Spindle.Windows.Model;

/// <summary>
/// Something about a proposal the person should know before adding it, as the server words it. A blocking one keeps it
/// from being added as it is; it can still be edited or thrown away.
/// </summary>
public sealed record ProposalIssue(string Message, bool Blocking);

public static class ProposalIssues
{
    /// <summary>The parts a request saves: two or more for a split, otherwise the one part that is the whole entry.</summary>
    public static SplitPart[] Parts(TimeEntryRequest request) => request.Shares is { Length: > 1 } shares
        ? shares.Select(s => new SplitPart(s.ProjectId, s.TaskId, s.IsBillable ?? false, s.Percent, s.Description)).ToArray()
        : [new SplitPart(request.ProjectId, request.TaskId, request.IsBillable ?? false, 100)];
}
