using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Spindle.Activity;

/// <summary>A repository as the server knows it: by identity and folder name, never by path.</summary>
public sealed record UploadRepository(string Identity, string Name);

/// <summary>An interval in which the agent was busy, in Unix milliseconds.</summary>
public sealed record UploadRun(long Start, long End);

/// <summary>
/// A session as it is uploaded when the person turned on activity sync, as the Mac's <c>AgentSessionUpload</c>: when
/// they sent prompts, when the agent was busy, and in which repository. Prompts, answers and folder paths never go.
/// </summary>
public sealed record AgentSessionUpload(string Source, string SessionId, string? ParentSessionId, UploadRepository? Repository,
    string? Title, IReadOnlyList<string> Branches, IReadOnlyList<long> Touches, IReadOnlyList<UploadRun> Runs, int ParserVersion)
{
    // The server's limits, which count UTF-16 units, so one odd session can't hold up the rest of a batch.
    public const int SessionIdMaxLength = 128;
    public const int NameMaxLength = 200;
    public const int TitleMaxLength = 200;
    public const int BranchMaxLength = 255;
    public const int MaxBranches = 50;

    /// <summary>Events less than this far apart are one run, as <c>ActivityTimeline.runGap</c> on the Mac.</summary>
    public const long RunGap = 3 * 60 * 1000;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// What a session says to the server: touches as timestamps, every other event only as runs, the title only when
    /// allowed. Null when there is nothing to say, or the server would not take its ID. A session in a repository the
    /// person ignores (<paramref name="ignored"/>) says only when its prompts were sent, which still end the attention
    /// before them, as a folder outside git's do; a subagent's says nothing.
    /// </summary>
    public static AgentSessionUpload? Create(LocalAgentSession session, AgentRepository? repository, bool titles, bool ignored = false)
    {
        if (session.Id.Length is 0 or > SessionIdMaxLength || session.Events.Count == 0) return null;
        var events = session.Events.OrderBy(e => e.At).ToArray();
        if (ignored)
        {
            var touches = session.ParentId is null ? events.Where(e => e.IsTouch).Select(e => e.At).ToArray() : [];
            return touches.Length == 0 ? null
                : new(AgentLogFormat.Of(session.Source).Id, session.Id, null, null, null, [], touches, [], AgentLogParser.Version);
        }
        var runs = new List<UploadRun>();
        long start = events[0].At, end = start;
        foreach (var item in events.Skip(1))
        {
            if (item.At - end > RunGap)
            {
                runs.Add(new(start, end));
                start = item.At;
            }
            end = item.At;
        }
        runs.Add(new(start, end));
        var title = Limited(session.Title.Trim(), TitleMaxLength);
        return new(AgentLogFormat.Of(session.Source).Id, session.Id,
            session.ParentId is { Length: > 0 and <= SessionIdMaxLength } parent ? parent : null,
            repository is null ? null : new(repository.Identity, Limited(repository.Name, NameMaxLength) is { Length: > 0 } name ? name : "Repository"),
            titles && title.Length > 0 ? title : null,
            session.Branches.Where(b => b.Length is > 0 and <= BranchMaxLength).Distinct().Order(StringComparer.Ordinal).Take(MaxBranches).ToArray(),
            events.Where(e => e.IsTouch).Select(e => e.At).ToArray(), runs, AgentLogParser.Version);
    }

    /// <summary>What was sent, to tell whether the session changed since.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string Fingerprint => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(this, Json)).AsSpan(0, 12));

    /// <summary>At most <paramref name="units"/> UTF-16 units, cut between characters.</summary>
    internal static string Limited(string text, int units)
    {
        if (text.Length <= units) return text;
        var result = new StringBuilder();
        foreach (var rune in text.EnumerateRunes())
        {
            if (result.Length + rune.Utf16SequenceLength > units) break;
            result.Append(rune.ToString());
        }
        return result.ToString();
    }
}

/// <summary>What an upload did: sessions the server took, and sessions it refused as invalid.</summary>
public readonly record struct AgentUploadResult(int Sent, int Rejected);

/// <summary>The server answered 400 to an upload: its sessions are invalid, whatever is sent again.</summary>
public sealed class AgentActivityUploadRejected(string message) : Exception(message);

/// <summary>
/// Uploads agent activity that changed since the last upload, for the person's own account while they allow it, as
/// the Mac's <c>ActivityUploader</c>. It remembers per session what it sent in a small file, so only changed sessions
/// go; after sync was turned on again, titles were allowed, or the parser changed, everything goes again.
/// </summary>
public sealed class AgentActivityUploader(string? file)
{
    /// <summary>How far back activity goes when sync is turned on.</summary>
    public static readonly TimeSpan History = TimeSpan.FromDays(90);
    /// <summary>The server keeps nothing older.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(400);
    /// <summary>The most sessions in one request.</summary>
    public const int BatchSize = 100;

    /// <summary>What was sent, and under which settings.</summary>
    internal sealed class State
    {
        public DateTimeOffset? SyncedSince { get; set; }
        public bool Titles { get; set; }
        public int ParserVersion { get; set; } = AgentLogParser.Version;
        /// <summary>Where the activity read begins: 90 days before the first upload, which stays so sessions stay whole.</summary>
        public DateTimeOffset? HistoryStart { get; set; }
        /// <summary>When the last look at the logs began; the next one reads only logs written since.</summary>
        public DateTimeOffset? ScannedAt { get; set; }
        /// <summary>The sources read (their ids); null in state written before there were more than Claude Code and Codex.</summary>
        public List<string>? Sources { get; set; }
        /// <summary>The hashes of the repositories ignored then; null in state from before there was an ignore list.</summary>
        public List<string>? Ignored { get; set; }
        public Dictionary<string, string> Sent { get; set; } = [];
    }

    private State state = Load(file);

    internal State Current => state;

    private static State Load(string? file)
    {
        try { return file is not null && File.Exists(file) ? JsonSerializer.Deserialize<State>(File.ReadAllText(file)) ?? new() : new(); }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException) { return new(); }
    }

    /// <summary>
    /// Uploads the sessions that changed and returns how many went and how many the server refused.
    /// <paramref name="read"/> reads whole sessions with events since a moment, from the logs written since another, if
    /// given; <paramref name="send"/> uploads a batch and throws <see cref="AgentActivityUploadRejected"/> when the
    /// server refuses it as invalid. Then the batch's sessions go one at a time: a session still refused counts as
    /// handled until it changes, so one bad session never holds up the ones after it. Sessions in repositories
    /// <paramref name="ignoring"/> names go as their prompts' times alone.
    /// </summary>
    public async Task<AgentUploadResult> UploadAsync(DateTimeOffset syncedSince, bool titles, DateTimeOffset now,
        Func<DateTimeOffset, DateTimeOffset?, CancellationToken, Task<IReadOnlyList<(LocalAgentSession Session, AgentRepository? Repository)>>> read,
        Func<IReadOnlyList<AgentSessionUpload>, CancellationToken, Task> send, CancellationToken cancellationToken = default,
        IReadOnlySet<AgentSource>? sources = null, IgnoredRepositories? ignoring = null)
    {
        ignoring ??= IgnoredRepositories.None;
        if (state.SyncedSince != syncedSince || state.Titles != titles || state.ParserVersion != AgentLogParser.Version)
            state = new State { SyncedSince = syncedSince, Titles = titles };
        // A source turned on (or added by an update) has sessions older than the last look; the ones already sent are
        // skipped below.
        var reading = (sources ?? AgentLogFormat.All.Select(f => f.Source).ToHashSet()).Select(s => AgentLogFormat.Of(s).Id).Order(StringComparer.Ordinal).ToList();
        if (!reading.All((state.Sources ?? ["claude", "codex"]).Contains)) state.ScannedAt = null;
        state.Sources = reading;
        // A repository ignored or no longer ignored since changes sessions older than the last look.
        var ignored = ignoring.Hashes.Order(StringComparer.Ordinal).ToList();
        if (!(state.Ignored ?? []).SequenceEqual(ignored)) state.ScannedAt = null;
        state.Ignored = ignored;
        state.HistoryStart ??= now - History;
        var from = new[] { state.HistoryStart.Value, now - Retention }.Max();
        // A minute's overlap, for logs written while the last look was under way.
        var known = new Dictionary<string, bool>(StringComparer.Ordinal);
        var uploads = (await read(from, state.ScannedAt?.AddMinutes(-1), cancellationToken))
            .Select(s => (s.Session.Id, Upload: AgentSessionUpload.Create(s.Session, s.Repository, titles, s.Repository is { } repository
                && (known.TryGetValue(repository.Identity, out var hit) ? hit : known[repository.Identity] = ignoring.Contains(repository)))))
            .ToArray();
        // A session with nothing to say isn't sent. One that was (before its repository was ignored, after which the
        // server deleted it) has to be sent again once it says something.
        foreach (var (id, _) in uploads.Where(u => u.Upload is null)) state.Sent.Remove(id);
        var changed = uploads.Select(u => u.Upload)
            .OfType<AgentSessionUpload>()
            .Select(upload => (Upload: upload, Fingerprint: upload.Fingerprint))
            .Where(s => !state.Sent.TryGetValue(s.Upload.SessionId, out var sent) || sent != s.Fingerprint)
            .OrderBy(s => s.Upload.Runs.Count > 0 ? s.Upload.Runs[0].Start : s.Upload.Touches[0])
            .ToArray();
        int sentCount = 0, rejected = 0;
        foreach (var batch in changed.Chunk(BatchSize))
        {
            try
            {
                await send(batch.Select(s => s.Upload).ToArray(), cancellationToken);
                foreach (var (upload, fingerprint) in batch) state.Sent[upload.SessionId] = fingerprint;
                sentCount += batch.Length;
                Save();
                continue;
            }
            catch (AgentActivityUploadRejected) { }
            foreach (var (upload, fingerprint) in batch)
            {
                try
                {
                    // A batch of one was just refused; asking again would say the same.
                    if (batch.Length == 1) throw new AgentActivityUploadRejected("Refused.");
                    await send([upload], cancellationToken);
                    sentCount++;
                }
                catch (AgentActivityUploadRejected)
                {
                    rejected++;
                }
                state.Sent[upload.SessionId] = fingerprint;
                Save();
            }
        }
        state.ScannedAt = now;
        Save();
        return new(sentCount, rejected);
    }

    /// <summary>Forgets what was sent, e.g. once sync is turned off and the server deleted it.</summary>
    public void Reset()
    {
        state = new State();
        try { if (file is not null) File.Delete(file); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private void Save()
    {
        if (file is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
            var temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(state));
            File.Move(temporary, file, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}
