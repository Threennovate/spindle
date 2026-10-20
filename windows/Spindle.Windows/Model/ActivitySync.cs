using Spindle.Activity;
using Spindle.Core;

namespace Spindle.Windows.Model;

/// <summary>
/// When the person turned on "Sync agent activity to your account", uploads what this PC read from its agents'
/// logs, as the Mac does, so the web and the server's MCP see the evidence of Windows-only people too: when
/// they sent prompts, when agents were busy, and each repository's name, git origin and branches. Never folder paths,
/// prompts or answers. The switch is an account setting; turning it off deletes what the server has, so this only
/// stops. Nothing is read or sent without the local consent to read the logs.
/// </summary>
public sealed class ActivitySync
{
    /// <summary>Uploads at most this often; logs written since the last upload are found by their time, so nothing is read again.</summary>
    internal static readonly TimeSpan UploadInterval = TimeSpan.FromMinutes(5);
    /// <summary>The account's setting is read again this often while sync is off, in case it was turned on elsewhere.</summary>
    internal static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(15);

    private readonly MainWindow owner;
    // Its own reader, so uploading doesn't throw away what the split suggestions cached, and the other way round.
    private readonly AgentActivityService reader;
    private readonly AgentActivityUploader uploader;
    private readonly string deviceFile;
    private AgentActivitySettingsResponse? account;
    private DateTimeOffset lastUpload = DateTimeOffset.MinValue;
    private DateTimeOffset lastCheck = DateTimeOffset.MinValue;
    private bool? wasSyncing;
    private bool working;

    public ActivitySync(MainWindow owner, string dataDirectory, string? home)
    {
        this.owner = owner;
        reader = new AgentActivityService(home);
        uploader = new AgentActivityUploader(Path.Combine(dataDirectory, "AgentActivityUploads.json"));
        deviceFile = Path.Combine(dataDirectory, "AgentActivityDevice.txt");
    }

    /// <summary>Why the last upload failed, to show in settings; null after one worked.</summary>
    public string? Problem { get; private set; }
    public event Action? Changed;

    /// <summary>The account's setting: what the server last said, or what it said when this person signed in.</summary>
    private AgentActivitySettingsResponse? Account => account ?? owner.Me?.AgentActivity;

    /// <summary>Whether this person's devices upload agent activity, as their account says.</summary>
    public bool IsSyncing => Account?.Sync ?? false;
    public bool IncludesTitles => Account?.Titles ?? false;

    /// <summary>A stable name for this PC, so its sessions are told apart from another device's.</summary>
    internal Guid Device()
    {
        try
        {
            if (File.Exists(deviceFile) && Guid.TryParse(File.ReadAllText(deviceFile).Trim(), out var known)) return known;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        var device = Guid.NewGuid();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(deviceFile)!);
            File.WriteAllText(deviceFile, device.ToString("D"));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        return device;
    }

    /// <summary>Uploads soon, e.g. once the timer stopped, so the web sees the entry's activity.</summary>
    public void UploadSoon() => lastUpload = DateTimeOffset.MinValue;

    /// <summary>Drops what was read, e.g. when reading the logs is turned off or the person signs out.</summary>
    public void Clear() => reader.Clear();

    /// <summary>Forgets the account and what was sent, e.g. after signing out.</summary>
    public void Forget()
    {
        reader.Clear();
        uploader.Reset();
        account = null;
        wasSyncing = null;
        Problem = null;
        lastUpload = lastCheck = DateTimeOffset.MinValue;
    }

    /// <summary>Called every few seconds: reads the account's setting and uploads what is due.</summary>
    public async Task TickAsync(DateTimeOffset now)
    {
        if (working || owner.Api is not { } api || owner.Me is not { } me || !owner.AgentLogsEnabled) return;
        if (IsSyncing ? now - lastUpload < UploadInterval : now - lastCheck < CheckInterval) return;
        working = true;
        try
        {
            lastCheck = now;
            await RefreshAccountAsync(api);
            if (Account is { Sync: true, SyncedSince: { } since } settings) await UploadAsync(api, me.Id, since, settings.Titles, now);
        }
        finally { working = false; }
    }

    /// <summary>Reads the account's setting again; once sync is off, what was sent is forgotten.</summary>
    private async Task RefreshAccountAsync(SpindleApiClient api)
    {
        try
        {
            account = await api.GetAgentActivitySettingsAsync();
        }
        catch (Exception)
        {
            // The setting last known stands until the server answers.
        }
        if (wasSyncing == true && !IsSyncing)
        {
            // Turned off elsewhere: the server deleted what was sent, so all of it goes again next time.
            uploader.Reset();
            Changed?.Invoke();
        }
        wasSyncing = IsSyncing;
    }

    private async Task UploadAsync(SpindleApiClient api, Guid accountId, DateTimeOffset since, bool titles, DateTimeOffset now)
    {
        var device = Device();
        var sources = owner.AgentSources;
        reader.Sources = sources;
        // Consent, sources or account settings that changed while reading or sending make the upload obsolete.
        void RequireCurrent()
        {
            if (owner.Me?.Id != accountId || !owner.AgentLogsEnabled || !owner.AgentSources.SetEquals(sources) || Account is not { Sync: true } current
                || current.SyncedSince != since || current.Titles != titles) throw new OperationCanceledException();
        }
        lastUpload = now;
        try
        {
            var result = await uploader.UploadAsync(since, titles, now,
                async (from, changed, ct) =>
                {
                    // Off the window's thread: the first look at 90 days of logs takes a while.
                    var sessions = await Task.Run(() => reader.ReadSessionsAsync(from, changed, ct), ct);
                    RequireCurrent();
                    return sessions;
                },
                async (batch, ct) =>
                {
                    RequireCurrent();
                    try
                    {
                        await api.UploadAgentSessionsAsync(device, batch.Cast<object>().ToArray(), ct);
                    }
                    catch (ApiException error) when (error.StatusCode == System.Net.HttpStatusCode.BadRequest)
                    {
                        throw new AgentActivityUploadRejected("The server refused the sessions.");
                    }
                }, sources: sources);
            Problem = result.Rejected > 0 ? "Some sessions couldn't be uploaded: the server refused them." : null;
        }
        catch (OperationCanceledException)
        {
            lastUpload = DateTimeOffset.MinValue;
            return;
        }
        catch (ApiException error) when (error.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            // Sync was turned off elsewhere.
            await RefreshAccountAsync(api);
        }
        catch (Exception error)
        {
            Problem = string.IsNullOrWhiteSpace(error.Message) ? "The activity couldn't be uploaded." : error.Message;
        }
        Changed?.Invoke();
    }

    /// <summary>A change of the setting is under way, so the switches wait for it.</summary>
    public bool IsChanging { get; private set; }

    /// <summary>Turns uploading on or off for all of the person's devices. Off deletes what was uploaded.</summary>
    public async Task ChangeAsync(bool sync, bool titles)
    {
        var api = owner.Api ?? throw new InvalidOperationException("Sign in first.");
        if (IsChanging) return;
        IsChanging = true;
        Changed?.Invoke();
        try
        {
            account = await api.UpdateAgentActivitySettingsAsync(new AgentActivitySettingsRequest { Sync = sync, Titles = sync && titles });
            wasSyncing = account.Sync;
            if (!sync) uploader.Reset();
            Problem = null;
            UploadSoon();
        }
        finally
        {
            IsChanging = false;
            Changed?.Invoke();
        }
    }
}
