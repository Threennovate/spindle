using System.Buffers;
using System.Text.Json;

namespace Spindle.Activity;

/// <summary>
/// Reads local metadata only when called. The application controls consent and should discard a result if consent
/// changes during a read. Only a bounded metadata cache is retained in memory; no prompt excerpts, network requests or disk ledger are created.
/// </summary>
public sealed class AgentActivityService(string? homePath = null)
{
    public const int MaximumLineBytes = 32 * 1024 * 1024;
    private readonly string home = homePath ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public bool HasLogs => AgentLogFormat.All.Any(f => Directory.Exists(f.LogsPath(home)));
    private IReadOnlySet<AgentSource> sources = AgentLogFormat.All.Select(f => f.Source).ToHashSet();
    /// <summary>The sources whose logs are read; changing them drops what was read, as <see cref="Clear"/> does.</summary>
    public IReadOnlySet<AgentSource> Sources
    {
        get { lock (cacheGate) return sources; }
        set { lock (cacheGate) { if (sources.SetEquals(value)) return; sources = value.ToHashSet(); generation++; cache.Clear(); } }
    }
    private IgnoredRepositories ignoring = IgnoredRepositories.None;
    /// <summary>
    /// The repositories to leave out of what is read: only their prompts' place in the attention stays
    /// (<see cref="ActivitySnapshot.Ignored"/>). The logs stay as they are, so a repository taken off the list comes back.
    /// </summary>
    public IgnoredRepositories Ignoring
    {
        get { lock (cacheGate) return ignoring; }
        set { lock (cacheGate) ignoring = value; }
    }
    private sealed record CachedLog(long From, long To, long Offset, long Length, DateTime Modified, AgentLogParser Parser);
    private readonly Dictionary<string, CachedLog> cache = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim readGate = new(1, 1);
    private readonly object cacheGate = new();
    private int generation;
    /// <summary>Drops all in-memory metadata. Callers should discard their displayed snapshot as well.</summary>
    public void Clear() { lock (cacheGate) { generation++; cache.Clear(); } }

    public async Task<ActivitySnapshot> ReadAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken = default)
    {
        await readGate.WaitAsync(cancellationToken);
        try { return await ReadCoreAsync(from, to, cancellationToken); }
        finally { readGate.Release(); }
    }

    private async Task<ActivitySnapshot> ReadCoreAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        if (to <= from) return ActivitySnapshot.Empty;
        var first = from.ToUnixTimeMilliseconds();
        var last = to.ToUnixTimeMilliseconds();
        // Cache a stable UTC-day window so a running timer can grow without re-reading historical records.
        // The result is still clipped to the exact caller range below.
        var windowFrom = new DateTimeOffset(from.UtcDateTime.Date, TimeSpan.Zero).ToUnixTimeMilliseconds() - AttentionTimeline.IdleCap;
        var windowTo = new DateTimeOffset(to.UtcDateTime.Date.AddDays(1), TimeSpan.Zero).ToUnixTimeMilliseconds();
        var (sessions, errors, readGeneration, _) = await GatherAsync(windowFrom, windowTo, null, cancellationToken);
        var repositoryOf = RepositoryOf(sessions);
        var ignored = Ignoring;
        var ignoredKnown = new Dictionary<string, bool>(StringComparer.Ordinal);
        bool IsIgnored(AgentRepository? repository) =>
            repository is not null && (ignoredKnown.TryGetValue(repository.Identity, out var known) ? known : ignoredKnown[repository.Identity] = ignored.Contains(repository));
        // Ignored sessions' prompts stay, for their place in the attention.
        var touches = new List<AgentTouch>();
        var runs = new List<AgentRun>();
        var shown = new List<LocalAgentSession>();
        foreach (var session in sessions.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var repository = repositoryOf(session);
            if (IsIgnored(repository))
            {
                // Only where its prompts were: they end the attention before them, as a folder outside git's do.
                if (session.ParentId is null)
                    touches.AddRange(session.Events.Where(e => e.IsTouch && e.At >= first - AttentionTimeline.IdleCap && e.At < last)
                        .Select(e => new AgentTouch(e.At, session.Id, repository)));
                continue;
            }
            shown.Add(session);
            if (session.ParentId is null)
                touches.AddRange(session.Events.Where(e => e.IsTouch && e.At >= first - AttentionTimeline.IdleCap && e.At < last)
                    .Select(e => new AgentTouch(e.At, session.Id, repository, session.Branches)));
            var events = session.Events.Where(e => e.At >= first - AttentionTimeline.IdleCap && e.At < last + 3 * 60 * 1000).OrderBy(e => e.At).ToArray();
            if (events.Length == 0) continue;
            var start = events[0].At;
            var end = start;
            void AddRun()
            {
                var a = Math.Max(start, first); var b = Math.Min(end, last);
                if (a < b || a == b && a >= first && a < last && start == end)
                    runs.Add(new(session.Id, session.Source, repository, a, b));
            }
            foreach (var item in events.Skip(1))
            {
                if (item.At - end > 3 * 60 * 1000) { AddRun(); start = item.At; }
                end = item.At;
            }
            AddRun();
        }
        lock (cacheGate) { if (readGeneration != generation) return ActivitySnapshot.Empty; }
        var (attention, ignoredAttention) = AttentionTimeline.Build(touches, first, last, IsIgnored);
        return new(shown.Select(s => s with { Events = s.Events.Where(e => e.At >= first - AttentionTimeline.IdleCap && e.At < last).ToArray() })
                .Where(s => s.Events.Count > 0).ToArray(),
            attention, runs.OrderBy(r => r.Start).ToArray(), errors) { Ignored = ignoredAttention };
    }

    /// <summary>
    /// Reads the logs whose events fall in a window, into sessions. With <paramref name="changedSince"/> only logs
    /// written since then are read, and all logs of a Claude Code session with them, so a session is never partial.
    /// <c>Own</c> are the sessions of the logs read for their own sake, before any parents were added.
    /// </summary>
    private async Task<(Dictionary<string, LocalAgentSession> Sessions, List<string> Errors, int Generation, HashSet<string> Own)> GatherAsync(
        long windowFrom, long windowTo, DateTimeOffset? changedSince, CancellationToken cancellationToken)
    {
        int readGeneration; IReadOnlySet<AgentSource> reading;
        lock (cacheGate) { readGeneration = generation; reading = sources; }
        var formats = AgentLogFormat.All.Where(f => reading.Contains(f.Source)).ToArray();
        var sessions = new Dictionary<string, LocalAgentSession>(StringComparer.Ordinal);
        var errors = new List<string>();
        foreach (var format in formats)
        {
            foreach (var file in Changed(format, format.LogsPath(home), format.Files(home), changedSince))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await ReadLogAsync(format.Source, file, windowFrom, windowTo, readGeneration, sessions, errors, cancellationToken);
            }
        }
        var own = sessions.Keys.ToHashSet(StringComparer.Ordinal);
        if (changedSince is not null) await ReadParentsAsync(formats, windowFrom, windowTo, readGeneration, sessions, errors, cancellationToken);
        // Last row wins: Codex appends renamed thread titles to its small index.
        foreach (var format in formats)
        {
            if (format.TitleIndex is not { } name || Path.Combine(format.Root(home), name) is not { } indexPath || !File.Exists(indexPath)) continue;
            try
            {
                await using var index = new FileStream(indexPath, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                await ReadLinesAsync(index, line => {
                    if (AgentLogParser.IndexTitle(line, format.Source) is { } row && sessions.TryGetValue(row.Id, out var session))
                        sessions[row.Id] = session with { Title = row.Title };
                }, cancellationToken: cancellationToken);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { if (errors.Count < 100) errors.Add($"Unable to read {format.Label} session titles."); }
        }
        return (sessions, errors, readGeneration, own);
    }

    /// <summary>Reads one log into the sessions, through the cache, so a log unchanged since is not read again.</summary>
    private async Task ReadLogAsync(AgentSource source, string file, long windowFrom, long windowTo, int readGeneration,
        Dictionary<string, LocalAgentSession> sessions, List<string> errors, CancellationToken cancellationToken)
    {
        try
        {
        CachedLog? cached;
                lock (cacheGate) { cache.TryGetValue(file, out cached); cache.Remove(file); }
                var info = new FileInfo(file);
                // Same-length edits or truncation reset the parser; appended logs keep their unfinished turn state.
                var reusable = cached is not null && cached.From == windowFrom && cached.To == windowTo
                    && info.Length >= cached.Length && (info.Length != cached.Length || info.LastWriteTimeUtc == cached.Modified);
                var parser = reusable ? cached!.Parser : new AgentLogParser(source, windowFrom, windowTo);
                    await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    if (reusable) stream.Position = cached!.Offset;
                    var offset = stream.Position;
                    var completeBytes = await ReadLinesAsync(stream, parser.Consume, () => {
                        if (errors.Count < 100) errors.Add($"Skipped an oversized record in {Path.GetFileName(file)}.");
                    }, cancellationToken);
                    lock (cacheGate)
                    {
                        if (readGeneration == generation)
                        {
                            cache[file] = new(windowFrom, windowTo, offset + completeBytes, info.Length, info.LastWriteTimeUtc, parser);
                            // The newest 256 logs / 100,000 events survive; older logs can be re-read on demand.
                            while (cache.Count > 256 || cache.Values.Sum(c => c.Parser.EventCount) > 100_000)
                                cache.Remove(cache.Keys.First());
                        }
                    }
                    foreach (var session in parser.Sessions)
                    {
                        // A root's metadata can be needed by its subagent even when it has no events in the range.
                        if (sessions.TryGetValue(session.Id, out var existing))
                            sessions[session.Id] = session with { Events = existing.Events.Concat(session.Events).Distinct().OrderBy(e => e.At).ToArray() };
                        else sessions[session.Id] = session;
                    }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    if (errors.Count < 100) errors.Add($"Unable to read {Path.GetFileName(file)}: {error.GetType().Name}.");
                }
    }

    /// <summary>
    /// A subagent works where its parent does, but the parent's log may not have changed and so was not read: read
    /// the logs of parents (and theirs) that are missing, for their folders only.
    /// </summary>
    private async Task ReadParentsAsync(IReadOnlyList<AgentLogFormat> formats, long windowFrom, long windowTo, int readGeneration,
        Dictionary<string, LocalAgentSession> sessions, List<string> errors, CancellationToken cancellationToken)
    {
        var logs = new Dictionary<AgentSource, string[]>();
        var attempted = new HashSet<string>(StringComparer.Ordinal);
        while (sessions.Values.Select(s => s.ParentId).FirstOrDefault(id => id is not null && !sessions.ContainsKey(id) && !attempted.Contains(id)) is { } missing)
        {
            cancellationToken.ThrowIfCancellationRequested();
            attempted.Add(missing);
            if (formats.FirstOrDefault(f => missing.StartsWith(f.Id + ":", StringComparison.Ordinal)) is not { } format) continue;
            // A parent's log is named after it, as Codex names its rollouts: rollout-<time>-<id>.jsonl.
            var raw = missing[(format.Id.Length + 1)..];
            if (!logs.TryGetValue(format.Source, out var files)) logs[format.Source] = files = format.Files(home).Select(f => f.FullName).ToArray();
            if (files.FirstOrDefault(f => Path.GetFileName(f).EndsWith(raw + ".jsonl", StringComparison.Ordinal)) is { } log)
                await ReadLogAsync(format.Source, log, windowFrom, windowTo, readGeneration, sessions, errors, cancellationToken);
        }
    }

    /// <summary>Log files to read: all, or those of the sessions written since a moment.</summary>
    private static IEnumerable<string> Changed(AgentLogFormat format, string folder, IEnumerable<FileInfo> files, DateTimeOffset? since)
    {
        if (since is not { } moment) return files.Select(f => f.FullName);
        var all = files.Select(f => (File: f, Group: format.Session(Path.GetRelativePath(folder, f.FullName)))).ToList();
        var changed = all.Where(f => f.File.LastWriteTimeUtc >= moment.UtcDateTime).Select(f => f.Group).ToHashSet(StringComparer.Ordinal);
        return all.Where(f => changed.Contains(f.Group)).Select(f => f.File.FullName);
    }

    /// <summary>The repository of a session: its root session's folder, since a subagent works where its parent does.</summary>
    private static Func<LocalAgentSession, AgentRepository?> RepositoryOf(IReadOnlyDictionary<string, LocalAgentSession> sessions)
    {
        var byPath = new Dictionary<string, AgentRepository?>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        return session =>
        {
            var seen = new HashSet<string> { session.Id };
            while (session.ParentId is { } parent && seen.Add(parent) && sessions.TryGetValue(parent, out var next)) session = next;
            if (!byPath.TryGetValue(session.Cwd, out var repository)) byPath[session.Cwd] = repository = RepositoryResolver.Resolve(session.Cwd);
            return repository;
        };
    }

    /// <summary>
    /// Whole sessions with events since <paramref name="from"/>, and where each ran, for uploading them. With
    /// <paramref name="changedSince"/> only sessions whose logs were written since then are read again.
    /// </summary>
    public async Task<IReadOnlyList<(LocalAgentSession Session, AgentRepository? Repository)>> ReadSessionsAsync(
        DateTimeOffset from, DateTimeOffset? changedSince = null, CancellationToken cancellationToken = default)
    {
        await readGate.WaitAsync(cancellationToken);
        try
        {
            // A stable UTC-day window, so parsers stay cached while it is not read again.
            var windowFrom = new DateTimeOffset(from.UtcDateTime.Date, TimeSpan.Zero).ToUnixTimeMilliseconds();
            var windowTo = new DateTimeOffset(DateTimeOffset.UtcNow.UtcDateTime.Date.AddDays(1), TimeSpan.Zero).ToUnixTimeMilliseconds();
            var (sessions, _, readGeneration, own) = await GatherAsync(windowFrom, windowTo, changedSince, cancellationToken);
            var repositoryOf = RepositoryOf(sessions);
            lock (cacheGate) { if (readGeneration != generation) throw new OperationCanceledException(); }
            var first = from.ToUnixTimeMilliseconds();
            // Parents read only to tell where a subagent worked are not uploaded: their logs did not change.
            return sessions.Values.Where(s => own.Contains(s.Id) && s.Events.Any(e => e.At >= first))
                .Select(s => (s with { Events = s.Events.Where(e => e.At >= first).ToArray() }, repositoryOf(s))).ToArray();
        }
        finally { readGate.Release(); }
    }

    /// <summary>Reads complete newline-terminated records; a live unfinished tail is read next refresh.</summary>
    public static async Task<long> ReadLinesAsync(Stream stream, Action<ReadOnlyMemory<byte>> consume,
        Action? oversized = null, CancellationToken cancellationToken = default, int maximumLineBytes = MaximumLineBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumLineBytes);
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        using var line = new MemoryStream();
        var skipping = false;
        long readBytes = 0, completedBytes = 0;
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, 64 * 1024), cancellationToken);
                if (read == 0) break;
                var offset = 0;
                while (offset < read)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var newline = Array.IndexOf(buffer, (byte)'\n', offset, read - offset);
                    var end = newline < 0 ? read : newline;
                    if (!skipping)
                    {
                        if (line.Length + end - offset > maximumLineBytes)
                        { skipping = true; line.SetLength(0); oversized?.Invoke(); }
                        else line.Write(buffer, offset, end - offset);
                    }
                    readBytes += end - offset + (newline >= 0 ? 1 : 0);
                    if (newline >= 0)
                    {
                        completedBytes = readBytes;
                        if (!skipping && line.Length > 0) consume(line.GetBuffer().AsMemory(0, (int)line.Length));
                        line.SetLength(0); skipping = false;
                    }
                    offset = end + 1;
                }
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
        return completedBytes;
    }
}
