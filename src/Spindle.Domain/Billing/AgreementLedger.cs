namespace Spindle.Domain.Billing;

/// <summary>A time entry on one of an agreement's tasks, as long as it counts.</summary>
/// <param name="Day">The day it starts on, in the calendar the ledger is kept in.</param>
public readonly record struct AgreementTime(Guid EntryId, Guid AgreementId, DateTimeOffset Start, DateOnly Day, long Seconds);

/// <summary>How much time one period of a retainer took, against the hours it includes.</summary>
/// <param name="IncludedSeconds">The time included, or null for a retainer without a limit.</param>
/// <param name="OverSeconds">The time beyond what is included.</param>
public sealed record RetainerUsage(Guid AgreementId, RetainerPeriod Period, long Seconds, long? IncludedSeconds, long OverSeconds);

/// <summary>
/// Retainers' time, period by period: how much each period used, and which entries went beyond the hours included.
/// Entries count in the order they started, so the one that crosses the limit is the first to be partly over it.
/// Time outside every period (before a retainer starts or after it ends) counts towards no period.
/// </summary>
public sealed class AgreementLedger
{
    private readonly Dictionary<Guid, long> over = [];
    private readonly Dictionary<Guid, List<RetainerUsage>> usage = [];

    private AgreementLedger()
    {
    }

    public static AgreementLedger Empty { get; } = new();

    /// <summary>Keeps the ledger for the retainers' periods that overlap the days from <paramref name="from"/> to <paramref name="to"/>.</summary>
    /// <param name="time">All the time on the retainers' tasks in those periods, whoever tracked it.</param>
    public static AgreementLedger Build(IEnumerable<Agreement> agreements, IEnumerable<AgreementTime> time, DateOnly from, DateOnly to)
    {
        var ledger = new AgreementLedger();
        var byAgreement = time.ToLookup(t => t.AgreementId);
        foreach (var agreement in agreements.Where(a => a.Kind == AgreementKind.Retainer))
        {
            var periods = agreement.Periods(from, to).ToList();
            var included = agreement.IncludedSeconds;
            var list = new List<RetainerUsage>(periods.Count);
            foreach (var period in periods)
            {
                long used = 0;
                foreach (var entry in byAgreement[agreement.Id].Where(t => period.Contains(t.Day)).OrderBy(t => t.Start).ThenBy(t => t.EntryId))
                {
                    var before = used;
                    used += entry.Seconds;
                    if (included is { } limit && used > limit)
                    {
                        ledger.over[entry.EntryId] = used - Math.Max(before, limit);
                    }
                }

                list.Add(new RetainerUsage(agreement.Id, period, used, included, included is { } cap ? Math.Max(0, used - cap) : 0));
            }

            ledger.usage[agreement.Id] = list;
        }

        return ledger;
    }

    /// <summary>How much of the entry went beyond the hours its retainer includes.</summary>
    public long OverSeconds(Guid entryId) => over.GetValueOrDefault(entryId);

    /// <summary>The retainer's periods, oldest first.</summary>
    public IReadOnlyList<RetainerUsage> Usage(Guid agreementId) => usage.TryGetValue(agreementId, out var list) ? list : [];
}
