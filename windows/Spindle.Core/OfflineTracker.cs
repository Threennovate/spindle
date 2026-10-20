using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Spindle.Core;

public enum PendingChangeKind { Create, Update, Stop, Delete }
public sealed record PendingChange(Guid Id, PendingChangeKind Kind, Guid WorkspaceId, Guid EntryId,
    TimeEntryResponse? Baseline, TimeEntryResponse? Desired, string? Error = null);

/// <summary>
/// Durable personal tracker cache and visible outbox. Creates reuse their GUID. Replayed edits carry their baseline's
/// version (If-Match), so the server refuses them when another device changed the entry since, and that becomes a
/// visible conflict instead of a silent overwrite.
/// </summary>
public sealed class OfflineTracker
{
    private readonly SpindleApiClient _client;
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private TrackerCache _cache = new();
    private TimeEntryResponse[] _entriesSnapshot = [];
    private PendingChange[] _pendingSnapshot = [];
    public event Action? Changed;
    public IReadOnlyList<PendingChange> PendingChanges => Volatile.Read(ref _pendingSnapshot).ToArray();
    public bool IsOffline { get; private set; }

    public OfflineTracker(SpindleApiClient client, string cacheDirectory, Guid userId)
    {
        _client = client;
        var server = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(client.ServerUri.AbsoluteUri))).ToLowerInvariant();
        _path = Path.Combine(cacheDirectory, server, userId.ToString("D"), "tracker.json");
    }

    public async Task LoadAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (File.Exists(_path))
                _cache = JsonSerializer.Deserialize<TrackerCache>(await File.ReadAllTextAsync(_path, ct), SpindleApiClient.JsonOptions)
                    ?? throw new InvalidDataException("The tracker cache could not be read.");
        }
        finally { _gate.Release(); }
        PublishSnapshot();
        Changed?.Invoke();
    }

    public TimeEntryResponse[] GetEntries(Guid workspaceId) => Volatile.Read(ref _entriesSnapshot).Where(e => e.WorkspaceId == workspaceId).OrderByDescending(e => e.Start).ToArray();
    public TimeEntryResponse? GetRunningEntry() => Volatile.Read(ref _entriesSnapshot).FirstOrDefault(e => e.Stop is null);

    public async Task<TimeEntryResponse> CreateAsync(Guid workspaceId, CreateTimeEntryRequest request, CancellationToken ct = default)
    {
        var start = request.Start ?? throw new ArgumentException("An entry needs a start time.", nameof(request));
        var entry = new TimeEntryResponse
        {
            Id = request.Id ?? Guid.NewGuid(), WorkspaceId = workspaceId,
            Description = request.Description?.Trim() ?? "", ProjectId = request.ProjectId, TaskId = request.TaskId,
            TagIds = request.TagIds?.Distinct().ToArray() ?? [], IsBillable = request.IsBillable ?? false,
            Shares = request.Shares?.Select(s => new TimeShareResponse { ProjectId = s.ProjectId, TaskId = s.TaskId, IsBillable = s.IsBillable ?? false, Percent = s.Percent, Description = s.Description }).ToArray() ?? [],
            Start = Precision(start), Stop = request.Stop is { } end ? Precision(end) : null,
        };
        var (shares, description) = SplitMath.Described(entry.Shares, entry.Description, request.DescribeParts, null);
        entry = MirrorLargestShare(entry with { Shares = shares, Description = description });
        ValidateEntry(entry);
        await _gate.WaitAsync(ct);
        try
        {
            if (entry.Stop is null && GetRunningEntry() is { } running)
            {
                if (entry.Start < running.Start) throw new ArgumentException("The next timer cannot start before the running timer.", nameof(request));
                AddPending(new(Guid.NewGuid(), PendingChangeKind.Stop, running.WorkspaceId, running.Id, running, running with { Stop = entry.Start }));
            }
            AddPending(new(Guid.NewGuid(), PendingChangeKind.Create, workspaceId, entry.Id, null, entry));
            await SaveAsync(ct);
            await ReconcileCoreAsync(ct);
            return _cache.Entries.FirstOrDefault(e => e.Id == entry.Id) ?? entry;
        }
        finally { _gate.Release(); Changed?.Invoke(); }
    }

    public async Task<TimeEntryResponse> UpdateAsync(TimeEntryResponse current, TimeEntryRequest request, CancellationToken ct = default)
    {
        var shares = request.Shares is null
            ? (current.ProjectId == request.ProjectId && current.TaskId == request.TaskId ? current.Shares : [])
            : request.Shares.Select(s => new TimeShareResponse { ProjectId = s.ProjectId, TaskId = s.TaskId, IsBillable = s.IsBillable ?? current.IsBillable, Percent = s.Percent, Description = s.Description }).ToArray();
        // The parts keep, gain or lose their own descriptions as the server decides, so the local entry shows what it will.
        (shares, var description) = SplitMath.Described(shares, request.Description ?? "", request.DescribeParts, current);
        var desired = MirrorLargestShare(current with
        {
            Description = description, ProjectId = request.ProjectId, TaskId = request.TaskId,
            TagIds = request.TagIds?.Distinct().ToArray() ?? [], IsBillable = request.IsBillable ?? current.IsBillable, Shares = shares,
            Start = Precision(request.Start ?? throw new ArgumentException("An entry needs a start time.", nameof(request))), Stop = request.Stop is { } end ? Precision(end) : null,
        });
        ValidateEntry(desired);
        if (current.Stop is not null && desired.Stop is null) throw new ArgumentException("A stopped entry cannot be restarted. Continue it instead.", nameof(request));
        await MutateAsync(new(Guid.NewGuid(), PendingChangeKind.Update, current.WorkspaceId, current.Id, current, desired), ct);
        return _cache.Entries.FirstOrDefault(e => e.Id == current.Id) ?? desired;
    }

    public async Task<TimeEntryResponse> StopAsync(TimeEntryResponse entry, DateTimeOffset stop, CancellationToken ct = default)
    {
        if (entry.Stop is not null) return entry;
        var desired = entry with { Stop = Precision(stop) };
        ValidateEntry(desired);
        await MutateAsync(new(Guid.NewGuid(), PendingChangeKind.Stop, entry.WorkspaceId, entry.Id, entry, desired), ct);
        return _cache.Entries.FirstOrDefault(e => e.Id == entry.Id) ?? desired;
    }

    public Task DeleteAsync(TimeEntryResponse entry, CancellationToken ct = default) =>
        MutateAsync(new(Guid.NewGuid(), PendingChangeKind.Delete, entry.WorkspaceId, entry.Id, entry, null), ct);

    private async Task MutateAsync(PendingChange change, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { AddPending(change); await SaveAsync(ct); await ReconcileCoreAsync(ct); }
        finally { _gate.Release(); Changed?.Invoke(); }
    }

    public async Task SynchronizeAsync(Guid workspaceId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await ReconcileCoreAsync(ct);
            if (!_client.IsAuthenticated) { IsOffline = true; return; }
            try
            {
                var entries = await _client.GetTimeEntriesAsync(workspaceId, from, to, ct);
                var running = await _client.GetCurrentTimeEntryAsync(ct);
                var protectedIds = _cache.Pending.Select(p => p.EntryId).ToHashSet();
                _cache.Entries.RemoveAll(e => e.WorkspaceId == workspaceId && e.Start >= from && e.Start < to && !protectedIds.Contains(e.Id));
                foreach (var entry in entries)
                    if (!protectedIds.Contains(entry.Id)) ReplaceEntry(entry);
                // A timer may belong to a workspace other than the one on screen.
                _cache.Entries.RemoveAll(e => e.Stop is null && e.Id != running?.Id && !protectedIds.Contains(e.Id));
                if (running is not null && !protectedIds.Contains(running.Id)) ReplaceEntry(running);
                IsOffline = false;
                await SaveAsync(ct);
            }
            catch (Exception ex) when (IsNetworkFailure(ex, ct)) { IsOffline = true; }
        }
        finally { _gate.Release(); Changed?.Invoke(); }
    }

    /// <summary>Discard the first failed change and its dependent local edits, restoring the server's current entry.</summary>
    public async Task DiscardPendingAsync(Guid changeId, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var change = _cache.Pending.FirstOrDefault(p => p.Id == changeId) ?? throw new ArgumentException("The pending change no longer exists.", nameof(changeId));
            var actual = await FindServerEntryAsync(change, ct);
            _cache.Pending.RemoveAll(p => p.EntryId == change.EntryId);
            _cache.Entries.RemoveAll(e => e.Id == change.EntryId);
            if (actual is not null) ReplaceEntry(actual);
            await SaveAsync(ct);
        }
        finally { _gate.Release(); Changed?.Invoke(); }
    }

    public async Task RetryPendingAsync(Guid changeId, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var index = _cache.Pending.FindIndex(p => p.Id == changeId);
            if (index < 0) throw new ArgumentException("The pending change no longer exists.", nameof(changeId));
            _cache.Pending[index] = _cache.Pending[index] with { Error = null };
            await SaveAsync(ct);
            await ReconcileCoreAsync(ct);
        }
        finally { _gate.Release(); Changed?.Invoke(); }
    }

    private async Task ReconcileCoreAsync(CancellationToken ct)
    {
        if (!_client.IsAuthenticated) { IsOffline = true; return; }
        foreach (var queued in _cache.Pending.ToArray())
        {
            // As it is now: an earlier change to the same entry may have given it a newer baseline version.
            var change = _cache.Pending.Find(p => p.Id == queued.Id) ?? queued;
            // Preserve order: a failed stop must not allow the next timer to start silently.
            if (change.Error is not null) break;
            try
            {
                TimeEntryResponse? result = null;
                if (change.Kind == PendingChangeKind.Create)
                {
                    var desired = change.Desired!;
                    result = await _client.CreateTimeEntryAsync(change.WorkspaceId, new CreateTimeEntryRequest
                    {
                        Id = desired.Id, Description = desired.Description, ProjectId = desired.ProjectId, TaskId = desired.TaskId,
                        TagIds = desired.TagIds, IsBillable = desired.IsBillable, Shares = RequestOf(desired).Shares, DescribeParts = RequestOf(desired).DescribeParts,
                        Start = desired.Start, Stop = desired.Stop,
                    }, ct);
                    if (!SameEntry(result, desired))
                        throw new ApiException(HttpStatusCode.Conflict, "This entry was already saved and then changed on another device. Review it before continuing.");
                }
                else result = await ReplayAsync(change, ct);
                _cache.Pending.RemoveAll(p => p.Id == change.Id);
                // Later edits of the entry were made on top of this one, so they now expect the version it got.
                if (result is not null)
                    for (var i = 0; i < _cache.Pending.Count; i++)
                        if (_cache.Pending[i] is { Baseline: { } later } pending && pending.EntryId == change.EntryId)
                            _cache.Pending[i] = pending with { Baseline = later with { Version = result.Version } };
                // Later queued edits already represent the local view; keep them until replayed.
                if (!_cache.Pending.Any(p => p.EntryId == change.EntryId))
                {
                    _cache.Entries.RemoveAll(e => e.Id == change.EntryId);
                    if (result is not null) ReplaceEntry(result);
                }
                IsOffline = false;
                await SaveAsync(ct);
            }
            catch (Exception ex) when (IsNetworkFailure(ex, ct)) { IsOffline = true; break; }
            catch (ApiException ex) when (ex.StatusCode is not HttpStatusCode.Unauthorized)
            {
                var index = _cache.Pending.FindIndex(p => p.Id == change.Id);
                _cache.Pending[index] = change with { Error = ex.Message };
                await SaveAsync(ct);
                break;
            }
        }
    }

    /// <summary>Sends an edit, stop or delete on condition that the entry still has its baseline's version.</summary>
    private async Task<TimeEntryResponse?> ReplayAsync(PendingChange change, CancellationToken ct)
    {
        const string changed = "This entry changed on another device. Review the pending change before continuing.";
        var baseline = change.Baseline!;
        if (string.IsNullOrEmpty(baseline.Version))
        {
            // Queued before entries had versions: compare once, then write on condition of the version compared.
            var actual = await FindServerEntryAsync(change, ct);
            if (change.Kind == PendingChangeKind.Delete && actual is null) return null;
            if (actual is not null && change.Desired is not null && SameEntry(actual, change.Desired)) return actual;
            if (actual is null || !SameEntry(actual, baseline)) throw new ApiException(HttpStatusCode.Conflict, changed);
            baseline = actual;
        }
        try
        {
            switch (change.Kind)
            {
                case PendingChangeKind.Delete: await _client.DeleteTimeEntryAsync(change.WorkspaceId, baseline, ct); return null;
                case PendingChangeKind.Stop: return await _client.StopTimeEntryAsync(change.WorkspaceId, change.EntryId, change.Desired!.Stop, ct, baseline.Version);
                default: return await _client.UpdateTimeEntryAsync(change.WorkspaceId, baseline, RequestOf(change.Desired!), ct);
            }
        }
        // Deleted already, e.g. on another device or by this delete before its answer was lost.
        catch (ApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound && change.Kind == PendingChangeKind.Delete) { return null; }
        // Saved already, e.g. by this change before its answer was lost.
        catch (ApiException ex) when (ex.Current is { } current && change.Desired is not null && SameEntry(current, change.Desired)) { return current; }
        catch (ApiException ex) when (ex.Current is not null || ex.StatusCode == HttpStatusCode.NotFound) { throw new ApiException(HttpStatusCode.Conflict, changed, ex.ProblemType); }
    }

    private Task<TimeEntryResponse?> FindServerEntryAsync(PendingChange change, CancellationToken ct) =>
        _client.GetTimeEntryAsync(change.WorkspaceId, change.EntryId, ct);

    private static void ValidateEntry(TimeEntryResponse entry)
    {
        if (entry.Id == Guid.Empty || entry.WorkspaceId == Guid.Empty) throw new ArgumentException("Entry and workspace IDs cannot be empty.");
        if (entry.Description.Length > 3000 || entry.Shares.Any(s => s.Description?.Length > 3000)) throw new ArgumentException("Description must be at most 3000 characters.");
        if (entry.Stop is { } stop && (stop < entry.Start || stop - entry.Start > TimeSpan.FromHours(999)))
            throw new ArgumentException("The end must be on or after the start, within 999 hours.");
        if (entry.Stop is null && entry.Start > DateTimeOffset.UtcNow.AddMinutes(1))
            throw new ArgumentException("A running timer cannot start in the future.");
        if (entry.TaskId is not null && entry.ProjectId is null) throw new ArgumentException("A task needs its project.");
        if (entry.TagIds.Length > 100 || entry.TagIds.Contains(Guid.Empty)) throw new ArgumentException("Choose at most 100 valid tags.");
        if (entry.Shares.Length != 0)
        {
            if (entry.Shares.Length is < 2 or > 6 || entry.Shares.Any(s => s.Percent is < 1 or > 99) || entry.Shares.Sum(s => s.Percent) != 100)
                throw new ArgumentException("Split time between 2–6 parts with positive percentages adding up to 100.");
            if (entry.Shares.DistinctBy(s => (s.ProjectId, s.TaskId)).Count() != entry.Shares.Length)
                throw new ArgumentException("Each split part needs a different project or task.");
            if (entry.Shares.Any(s => s.TaskId is not null && s.ProjectId is null)) throw new ArgumentException("Every split task needs its project.");
        }
    }

    private static bool IsNetworkFailure(Exception ex, CancellationToken ct) => ex is HttpRequestException || ex is TaskCanceledException && !ct.IsCancellationRequested
        || ex is ApiException api && (int)api.StatusCode >= 500;
    private void AddPending(PendingChange change)
    {
        _cache.Pending.Add(change);
        _cache.Entries.RemoveAll(e => e.Id == change.EntryId);
        if (change.Desired is not null) _cache.Entries.Add(change.Desired);
    }
    private void ReplaceEntry(TimeEntryResponse entry) { _cache.Entries.RemoveAll(e => e.Id == entry.Id); _cache.Entries.Add(entry); }
    private static TimeEntryResponse MirrorLargestShare(TimeEntryResponse entry)
    {
        if (entry.Shares.Length < 2) return entry;
        var largest = entry.Shares.OrderByDescending(s => s.Percent).First();
        return entry with { ProjectId = largest.ProjectId, TaskId = largest.TaskId, IsBillable = largest.IsBillable };
    }
    private static TimeEntryRequest RequestOf(TimeEntryResponse entry) => new()
    {
        Description = entry.Description, ProjectId = entry.ProjectId, TaskId = entry.TaskId, TagIds = entry.TagIds,
        IsBillable = entry.IsBillable, Start = entry.Start, Stop = entry.Stop,
        Shares = entry.Shares.Select(s => new TimeShareRequest { ProjectId = s.ProjectId, TaskId = s.TaskId, IsBillable = s.IsBillable, Percent = s.Percent, Description = s.Description }).ToArray(),
        // The local entry already shows what the server should keep, so replay says so rather than leave it to the server.
        DescribeParts = entry.Shares.Length > 1 ? SplitMath.DescribesParts(entry) : null,
    };
    // PostgreSQL timestamptz stores microseconds; .NET instants also contain tenths of a microsecond.
    private static DateTimeOffset Precision(DateTimeOffset value) => new(value.UtcTicks / 10 * 10, TimeSpan.Zero);
    private static bool SameEntry(TimeEntryResponse a, TimeEntryResponse b) =>
        a.Id == b.Id && a.WorkspaceId == b.WorkspaceId && a.Description == b.Description && a.ProjectId == b.ProjectId && a.TaskId == b.TaskId &&
        a.IsBillable == b.IsBillable && Precision(a.Start) == Precision(b.Start) &&
        (a.Stop is { } aStop ? Precision(aStop) : (DateTimeOffset?)null) == (b.Stop is { } bStop ? Precision(bStop) : (DateTimeOffset?)null) &&
        a.TagIds.Order().SequenceEqual(b.TagIds.Order()) && a.Shares.SequenceEqual(b.Shares);

    private async Task SaveAsync(CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        var temporary = _path + ".tmp";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(_cache, SpindleApiClient.JsonOptions);
        await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        { await stream.WriteAsync(bytes, ct); await stream.FlushAsync(ct); stream.Flush(flushToDisk: true); }
        File.Move(temporary, _path, overwrite: true);
        PublishSnapshot();
    }

    private void PublishSnapshot()
    {
        Volatile.Write(ref _entriesSnapshot, _cache.Entries.ToArray());
        Volatile.Write(ref _pendingSnapshot, _cache.Pending.ToArray());
    }

    public sealed class TrackerCache
    {
        public List<TimeEntryResponse> Entries { get; set; } = [];
        public List<PendingChange> Pending { get; set; } = [];
    }
}
