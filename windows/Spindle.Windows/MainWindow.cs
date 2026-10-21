using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Spindle.Activity;
using Spindle.Core;
using Spindle.Platform;
using Spindle.Windows.Desktop;
using Spindle.Windows.Model;
using Spindle.Windows.Views;
using Spindle.Windows.Views.Shell;

namespace Spindle.Windows;

/// <summary>The sections of the navigation pane, as the Mac's sidebar.</summary>
public enum Section { Timer, Calendar, Reports, Projects, Clients, Tags, Settings }

/// <summary>A page that follows the running timer and the entries as they change.</summary>
public interface ILivePage
{
    void Refresh();
    void Tick();
}

/// <summary>
/// The app's window and the signed-in session behind it: the account, the workspace, its catalog and the entries,
/// kept on this PC by the offline tracker and synced with the server. The views read from it and act through it.
/// </summary>
public sealed class MainWindow : Window
{
    private readonly string dataDirectory;
    private SpindleApiClient? api;
    private ISessionStore? sessionStore;
    private DesktopCache? desktopCache;
    private bool reviewing;
    private bool reviewingDays;
    private readonly PreferencesStore preferenceStore;
    private DesktopPreferences preferences;
    private MeResponse? me;
    private WorkspaceSummary? workspace;
    private readonly ContentControl body = new();
    private readonly SemaphoreSlim refreshGate = new(1, 1);
    private readonly SemaphoreSlim mutationGate = new(1, 1);
    private readonly DispatcherTimer pulse = new() { Interval = TimeSpan.FromSeconds(1) };
    private int ticks;
    private int sessionGeneration;
    private bool quitting;
    private IDisposable? shortcut;
    private LiveSync? liveSync;
    private ShellView? shell;
    private readonly ContentControl offerHost = new() { HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom, Margin = new Thickness(20, 0, 20, 76) };

    public MainWindow(SpindleApiClient? client = null, PreferencesStore? store = null, string? dataDirectory = null, Func<TimeSpan?>? idle = null)
    {
        this.dataDirectory = dataDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Spindle");
        api = client;
        preferenceStore = store ?? new PreferencesStore(Path.Combine(this.dataDirectory, "preferences.json"));
        preferences = preferenceStore.Load();
        Activity.Sources = preferences.Sources();
        Proposals = new ProposalInbox(this);
        AgentConnector = new AgentConnector(() => api?.ServerUri);
        Proposals.Changed += () => RefreshViews();
        Proposals.Arrived += ProposalsArrived;
        if (client is null && Environment.GetEnvironmentVariable("SPINDLE_SERVER_URL") is { Length: > 0 } address) preferences = preferences with { ServerUrl = address };
        Title = "Spindle";
        Width = 1100;
        Height = 760;
        MinWidth = 480;
        MinHeight = 560;
        Icon = new WindowIcon(Avalonia.Platform.AssetLoader.Open(new Uri("avares://Spindle/Assets/icon.png")));
        // Windows 11: Mica behind the navigation pane and the title bar drawn by the app, as in the system's own apps.
        TransparencyLevelHint = [WindowTransparencyLevel.Mica, WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.None];
        if (OperatingSystem.IsWindows())
        {
            ExtendClientAreaToDecorationsHint = true;
            ExtendClientAreaTitleBarHeightHint = ShellView.TitleBarHeight;
        }
        // Notices over dialogs too, so "Saved · Undo" can be clicked while the day's review is open.
        Content = new Panel { Children = { body, offerHost, Dialogs, Notices } };
        pulse.Tick += async (_, _) =>
        {
            UpdateClock();
            if (++ticks % 20 == 0 && workspace is not null) await RefreshSafelyAsync();
        };
        Opened += async (_, _) =>
        {
            ApplyBackdrop();
            Updates.Start();
            await InitializeAsync();
        };
        Activated += async (_, _) =>
        {
            if (workspace is not null) await RefreshSafelyAsync();
        };
        Closing += (_, e) =>
        {
            if (!quitting && preferences.MinimizeToTray && OperatingSystem.IsWindows())
            {
                e.Cancel = true;
                Hide();
            }
        };
        Closed += async (_, _) =>
        {
            pulse.Stop();
            shortcut?.Dispose();
            Activity.Clear();
            ActivitySync.Clear();
            if (liveSync is not null) await liveSync.DisposeAsync();
        };
        KeyDown += OnKeyDown;
        Splits = new SplitAdvisor(this);
        Splits.Changed += () => RefreshViews();
        ActivitySync = new ActivitySync(this, this.dataDirectory, Environment.GetEnvironmentVariable("SPINDLE_AGENT_HOME"));
        ActivitySync.Changed += () => Dispatcher.UIThread.Post(() => RefreshViews());
        Stopped += _ => ActivitySync.UploadSoon();
        Desktop = new DesktopService(this, idle);
        Desktop.OfferChanged += () => offerHost.Content = Desktop.Offer is { } offer ? new SplitOfferCard(this, offer)
            : Desktop.DriftOffer is { } drift ? new DriftOfferCard(this, drift) : null;
        ShowSignIn();
    }

    // What the views read

    public AgentActivityService Activity { get; } = new(Environment.GetEnvironmentVariable("SPINDLE_AGENT_HOME"));
    public SplitAdvisor Splits { get; private set; } = null!;
    /// <summary>Uploads agent activity to the account when the person turned that on.</summary>
    public ActivitySync ActivitySync { get; private set; } = null!;
    public DesktopService Desktop { get; private set; } = null!;
    public AppUpdates Updates { get; } = new();
    /// <summary>What agents proposed through the server's MCP, waiting for the person.</summary>
    public ProposalInbox Proposals { get; }
    /// <summary>Connects Claude Code and Codex to the server's MCP.</summary>
    public AgentConnector AgentConnector { get; }

    /// <summary>When the Pomodoro break ends, while one is on.</summary>
    public DateTimeOffset? BreakEnds { get; set; }
    public NoticeHost Notices { get; } = new();
    public DialogHost Dialogs { get; } = new();
    public OfflineTracker? Tracker { get; private set; }
    public SpindleApiClient? Api => api;
    public MeResponse? Me => me;
    public WorkspaceSummary? Workspace => workspace;
    public Guid WorkspaceId => workspace?.Id ?? Guid.Empty;
    public TimeEntryResponse? Running { get; private set; }
    public Catalog Catalog { get; private set; } = Catalog.Empty;
    public ProjectResponse[] Projects => Catalog.Projects;
    public TagResponse[] Tags => Catalog.Tags;
    public TimeEntryResponse[] Entries { get; private set; } = [];
    public EntryRules Rules { get; private set; } = new(new WorkspaceSettingsResponse(), false);
    public UserTime Time { get; private set; } = new(TimeZoneInfo.Local);
    public TimeZoneInfo Zone => Time.Zone;
    public int WeekStart => Time.WeekStart;
    public bool IsAdmin => workspace?.Role == WorkspaceRole.Admin;
    public DesktopPreferences Preferences => preferences;
    public bool AgentLogsEnabled => preferences.AgentLogsEnabled;
    /// <summary>The agents whose logs are read, when agent logs are.</summary>
    public IReadOnlySet<AgentSource> AgentSources => preferences.Sources();
    public bool MinimizeToTray => preferences.MinimizeToTray;
    public Section Section => shell?.Section ?? Section.Timer;
    public bool IsOffline => Tracker?.IsOffline == true;
    public int PendingCount => Tracker?.PendingChanges.Count ?? 0;

    /// <summary>The organization that owns this workspace, when it can no longer change time.</summary>
    public OrganizationSummary? ReadOnlyOrganization =>
        me?.Organizations.FirstOrDefault(o => o.Id == workspace?.OrganizationId && o.Access == OrganizationAccess.ReadOnly);

    public event Action? TimerChanged;

    public string FormatTime(DateTimeOffset value) => Time.Clock(value);
    public string FormatDuration(long seconds) => Time.Duration(seconds);

    // Signing in

    public async Task InitializeAsync()
    {
        try
        {
            api ??= NewClient(preferences.ServerUrl);
            if (api.IsAuthenticated || await api.RestoreSessionAsync()) await SignedInAsync();
        }
        catch (Exception ex)
        {
            if (ex is HttpRequestException or TaskCanceledException || ex is ApiException failure && (int)failure.StatusCode >= 500)
            {
                if (await OpenOfflineAsync()) return;
            }
            Notify(ex.Message);
        }
    }

    /// <summary>Opens the last signed-in workspace from this PC's copy when the server can't be reached.</summary>
    private async Task<bool> OpenOfflineAsync()
    {
        if (sessionStore is null || await sessionStore.LoadAsync() is null || desktopCache is null || await desktopCache.LoadAsync() is not { } cached) return false;
        me = cached.Me;
        workspace = me.Workspaces.FirstOrDefault(w => w.Id == cached.WorkspaceId);
        if (workspace is null) return false;
        Catalog = new Catalog(cached.Projects, cached.Clients ?? [], cached.Tags);
        Rules = new EntryRules(cached.Settings ?? new WorkspaceSettingsResponse(), IsAdmin);
        Time = UserTime.For(me);
        Tracker = new OfflineTracker(api!, Path.Combine(dataDirectory, "Tracking"), me.Id);
        await Tracker.LoadAsync();
        Entries = Tracker.GetEntries(workspace.Id);
        Running = Tracker.GetRunningEntry();
        BuildShell();
        await NavigateAsync(Section.Timer);
        pulse.Start();
        Desktop.Start();
        ApplyShortcut();
        Notify("You're offline. Changes are kept on this PC and sync when the server is back.", NoticeKind.Success);
        return true;
    }

    private SpindleApiClient NewClient(string address)
    {
        var uri = ServerUri(address);
        sessionStore = OperatingSystem.IsWindows() ? new CredentialSessionStore(uri) : new InMemorySessionStore();
        desktopCache = new DesktopCache(uri, dataDirectory);
        return new SpindleApiClient(new HttpClient { BaseAddress = uri, Timeout = TimeSpan.FromSeconds(30) }, sessionStore);
    }

    private static Uri ServerUri(string address)
    {
        if (!address.Contains("://", StringComparison.Ordinal)) address = "https://" + address;
        if (!Uri.TryCreate(address.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http") || !string.IsNullOrEmpty(uri.UserInfo))
            throw new InvalidOperationException("Enter a valid server address.");
        if (uri.Scheme == "http" && !uri.IsLoopback && !uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Use HTTPS, or localhost for development.");
        return new Uri(uri.GetLeftPart(UriPartial.Authority) + "/");
    }

    private void ShowSignIn()
    {
        MinWidth = 480;
        MinHeight = 560;
        shell = null;
        SpindleApiClient Client(Uri server)
        {
            if (api is null || api.ServerUri != server) api = NewClient(server.AbsoluteUri);
            return api;
        }
        async Task Complete(Uri server)
        {
            preferences = preferences with { ServerUrl = server.AbsoluteUri };
            preferenceStore.Save(preferences);
            await SignedInAsync();
        }
        var deviceName = "Spindle for Windows · " + Environment.MachineName;
        body.Content = new AuthView(api?.ServerUri ?? ServerUri(preferences.ServerUrl), ServerUri, Client,
            async (server, request) =>
            {
                await Client(server).LoginAsync(request with { DeviceName = deviceName });
                await Complete(server);
            },
            async (server, request) =>
            {
                await Client(server).RegisterAsync(request with { TimeZone = IanaZone(), DeviceName = deviceName });
                await Complete(server);
            },
            async (server, provider, ct) =>
            {
                // A new account lands where a registered one does: the server made it with this PC's time zone.
                var signedIn = await ExternalSignIn.RunAsync(Client(server), provider.Name, IanaZone(), deviceName, null, OpenBrowser, ct);
                // The browser has the focus now; the sign-in goes on here.
                Show();
                if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
                Activate();
                if (signedIn is not null) await Complete(server);
            });
    }

    /// <summary>Opens a page in the default browser. Tests replace it to play the browser's part.</summary>
    internal Action<Uri> OpenBrowser { get; set; } = uri => Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });

    private static string IanaZone()
    {
        var id = TimeZoneInfo.Local.Id;
        return TimeZoneInfo.TryConvertWindowsIdToIanaId(id, out var iana) ? iana : id;
    }

    private async Task SignedInAsync()
    {
        sessionGeneration++;
        Catalog = Catalog.Empty;
        Entries = [];
        me = await api!.GetMeAsync();
        Tracker = new OfflineTracker(api, Path.Combine(dataDirectory, "Tracking"), me.Id);
        await Tracker.LoadAsync();
        Time = UserTime.For(me);
        var workspaces = me.Workspaces;
        workspace = workspaces.FirstOrDefault(w => w.Id == preferences.WorkspaceId) ?? workspaces.FirstOrDefault(w => w.Id == me.DefaultWorkspaceId) ?? workspaces.FirstOrDefault();
        if (workspace is null)
        {
            body.Content = new NoWorkspaceView(() => OpenWeb(""), SignOutAsync);
            return;
        }
        BuildShell();
        pulse.Start();
        Desktop.Start();
        ApplyShortcut();
        await RefreshAsync();
        await NavigateAsync(Section.Timer);
        liveSync = new LiveSync(api);
        liveSync.Changed += change => Dispatcher.UIThread.Post(async () =>
        {
            // Links and their branch rules chosen on another device, or on the web.
            if (change.Topic == "agentActivity") await Splits.ReloadLinksAsync();
            await RefreshSafelyAsync();
        });
        liveSync.Reconnected += () => Dispatcher.UIThread.Post(async () => await RefreshSafelyAsync());
        try
        {
            await liveSync.StartAsync(workspace.Id);
        }
        catch (Exception)
        {
            // Changes from other devices still arrive: the window polls every 20 seconds and when activated.
        }
    }

    /// <summary>Switches to another of the person's workspaces; every section starts fresh in it.</summary>
    public async Task SwitchWorkspaceAsync(WorkspaceSummary target)
    {
        if (target.Id == workspace?.Id) return;
        workspace = target;
        preferences = preferences with { WorkspaceId = target.Id };
        preferenceStore.Save(preferences);
        Catalog = Catalog.Empty;
        Entries = [];
        BuildShell();
        await RefreshSafelyAsync();
        await NavigateAsync(shell!.Section);
        if (liveSync is not null)
        {
            try
            {
                await liveSync.WatchAsync(target.Id);
            }
            catch (Exception)
            {
                // Polling keeps the workspace current.
            }
        }
    }

    private void BuildShell()
    {
        MinWidth = 900;
        MinHeight = 600;
        Width = Math.Max(Width, 1100);
        Height = Math.Max(Height, 760);
        shell = new ShellView(this);
        body.Content = shell;
    }

    /// <summary>Mica where Windows offers it; the solid window colour everywhere else.</summary>
    private void ApplyBackdrop()
    {
        var mica = ActualTransparencyLevel == WindowTransparencyLevel.Mica || ActualTransparencyLevel == WindowTransparencyLevel.AcrylicBlur;
        if (mica) Background = Avalonia.Media.Brushes.Transparent;
        else this.Res(BackgroundProperty, "WindowBackgroundBrush");
    }

    // Navigation

    public Task NavigateAsync(Section target) => shell?.NavigateAsync(target) ?? Task.CompletedTask;

    public async Task NavigateSafelyAsync(Section target)
    {
        try
        {
            await NavigateAsync(target);
        }
        catch (Exception ex)
        {
            Notify(ex.Message);
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && Dialogs.IsVisible)
        {
            Dialogs.Close(null);
            e.Handled = true;
            return;
        }
        if (shell is null || Dialogs.IsVisible) return;
        var command = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        if (command && e.Key is >= Key.D1 and <= Key.D6)
        {
            _ = NavigateSafelyAsync((Section)(e.Key - Key.D1));
            e.Handled = true;
        }
        else if (command && e.Key == Key.N)
        {
            _ = FocusNewEntryAsync();
            e.Handled = true;
        }
        else if (command && e.Key == Key.R)
        {
            _ = RefreshSafelyAsync();
            e.Handled = true;
        }
        else if (command && e.Key == Key.OemComma)
        {
            _ = NavigateSafelyAsync(Section.Settings);
            e.Handled = true;
        }
    }

    /// <summary>Brings the timer forward, ready to type what the person is working on.</summary>
    public async Task FocusNewEntryAsync()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        if (shell is null) return;
        await NavigateAsync(Section.Timer);
        shell.TimerView?.FocusDescription();
    }

    // Settings stored on this PC

    public void SavePreferences(DesktopPreferences next)
    {
        if (OperatingSystem.IsWindows() && next.LaunchAtLogin != preferences.LaunchAtLogin) WindowsDesktop.SetLaunchAtLogin(next.LaunchAtLogin);
        if (!next.AgentLogsEnabled)
        {
            Activity.Clear();
            ActivitySync.Clear();
        }
        if (!next.Sources().SetEquals(preferences.Sources()))
        {
            // What was read came from other logs: the calendar, suggestions and uploads read again.
            Activity.Sources = next.Sources();
            Splits.ForgetActivity();
        }
        preferences = next;
        preferenceStore.Save(preferences);
        ApplyShortcut();
        PreferencesChanged?.Invoke();
    }

    public event Action? PreferencesChanged;

    /// <summary>Draws the sections again, e.g. after a choice kept in the preferences hides something.</summary>
    public void RefreshViews()
    {
        shell?.Refresh();
        Refreshed?.Invoke();
    }

    /// <summary>The entries, the catalog or the suggestions changed and the sections were drawn again, e.g. for a dialog to follow.</summary>
    public event Action? Refreshed;

    /// <summary>Keeps when running entries' splits were set, without anything else in the preferences changing.</summary>
    public void SaveSplitStatements(SplitStatements statements)
    {
        preferences = preferences with { SplitStatements = statements };
        preferenceStore.Save(preferences);
    }

    /// <summary>Keeps the ignore list, without anything else in the preferences changing.</summary>
    public void SaveIgnoredRepositories(IgnoredList ignored)
    {
        preferences = preferences with { IgnoredRepositories = ignored };
        preferenceStore.Save(preferences);
    }

    /// <summary>Why a shortcut couldn't be set, e.g. another app holds it; shown in Settings.</summary>
    public string? ShortcutProblem { get; private set; }

    /// <summary>The quick entry shortcut was pressed.</summary>
    public event Action? QuickEntryRequested;

    private void ApplyShortcut()
    {
        shortcut?.Dispose();
        shortcut = null;
        ShortcutProblem = null;
        if (!OperatingSystem.IsWindows()) return;
        var wanted = new (int Id, string? Text, string Name)[]
        {
            (1, preferences.QuickEntryShortcut, "Quick entry"),
            (2, preferences.ToggleTimerShortcut, "Start or stop the timer"),
            (3, preferences.NewEntryShortcut, "New entry"),
        };
        var keys = wanted.Select(w => (w.Id, Key: HotKeyText.Parse(w.Text))).Where(w => w.Key is not null).Select(w => (w.Id, w.Key!.Value.Modifiers, w.Key.Value.Key)).ToArray();
        if (keys.Length == 0) return;
        try
        {
            var hotKeys = new GlobalHotKeys(keys, id => Dispatcher.UIThread.Post(async () =>
            {
                try
                {
                    if (id == 1) QuickEntryRequested?.Invoke();
                    else if (id == 2) await ToggleTimerAsync();
                    else await FocusNewEntryAsync();
                }
                catch (Exception ex)
                {
                    Notify(ex.Message);
                }
            }));
            if (hotKeys.Refused.Count > 0)
                ShortcutProblem = "Another app has " + string.Join(" and ", wanted.Where(w => hotKeys.Refused.Contains(w.Id)).Select(w => $"{w.Text} ({w.Name})")) + ". Choose another.";
            shortcut = hotKeys;
        }
        catch (Exception ex)
        {
            ShortcutProblem = ex.Message;
        }
    }

    // Data

    public async Task RefreshSafelyAsync()
    {
        try
        {
            await RefreshAsync();
        }
        catch (Exception)
        {
            if (workspace is not null && Tracker is not null)
            {
                Entries = Tracker.GetEntries(workspace.Id);
                Running = Tracker.GetRunningEntry();
                RefreshViews();
                UpdateClock();
            }
        }
    }

    private async Task RefreshAsync()
    {
        if (api is null || workspace is null) return;
        await refreshGate.WaitAsync();
        if (workspace is null)
        {
            refreshGate.Release();
            return;
        }
        var id = workspace.Id;
        var generation = sessionGeneration;
        try
        {
            if (!api.IsAuthenticated && !await api.RestoreSessionAsync())
            {
                Notify("Your session expired. Sign in again to sync saved changes.");
                return;
            }
            var from = new DateTimeOffset(DateTime.UtcNow.AddDays(-35));
            var projectTask = api.GetProjectsAsync(id, true);
            var clientTask = api.GetClientsAsync(id, true);
            var tagTask = api.GetTagsAsync(id);
            var settingsTask = api.GetWorkspaceSettingsAsync(id);
            var entryTask = Tracker is null ? Task.CompletedTask : Tracker.SynchronizeAsync(id, from, DateTimeOffset.UtcNow.AddDays(1));
            await Task.WhenAll(projectTask, clientTask, tagTask, settingsTask, entryTask);
            if (workspace?.Id != id || generation != sessionGeneration) return;
            Catalog = new Catalog(await projectTask, await clientTask, await tagTask);
            Rules = new EntryRules(await settingsTask, IsAdmin);
            Entries = Tracker?.GetEntries(id) ?? [];
            Running = Tracker?.GetRunningEntry();
            if (desktopCache is not null && me is not null) await desktopCache.SaveAsync(new DesktopSnapshot(me, Catalog.Projects, Catalog.Tags, id, Catalog.Clients, Rules.Settings));
            // Proposals change on the server: agents make them, other devices answer them (sync), or a while passed.
            await Proposals.RefreshAsync();
            UpdateClock();
            RefreshViews();
        }
        finally
        {
            refreshGate.Release();
        }
    }

    /// <summary>Loads the entries of other days, e.g. the calendar's, into the tracker's copy.</summary>
    public async Task<TimeEntryResponse[]> LoadEntriesAsync(DateTimeOffset from, DateTimeOffset to)
    {
        if (api is null || workspace is null) return [];
        if (Tracker is null) return await api.GetTimeEntriesAsync(workspace.Id, from, to);
        try
        {
            await Tracker.SynchronizeAsync(workspace.Id, from, to);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // Offline: show what this PC has.
        }
        Entries = Tracker.GetEntries(workspace.Id);
        Running = Tracker.GetRunningEntry();
        return Entries.Where(e => e.Start < to && (e.Stop ?? DateTimeOffset.UtcNow) > from).ToArray();
    }

    private void UpdateClock()
    {
        shell?.Tick();
        TimerChanged?.Invoke();
    }

    // Changes to time entries

    /// <summary>Runs one change at a time and shows the result at once, before the server confirms it.</summary>
    public async Task MutateAsync(Func<Task> action)
    {
        if (!await mutationGate.WaitAsync(0))
        {
            Notify("Wait for the current change to finish.");
            return;
        }
        try
        {
            await action();
            if (workspace is not null && Tracker is not null)
            {
                Entries = Tracker.GetEntries(workspace.Id);
                Running = Tracker.GetRunningEntry();
                RefreshViews();
                UpdateClock();
            }
            await RefreshSafelyAsync();
        }
        finally
        {
            mutationGate.Release();
        }
    }

    public Task<TimeEntryResponse> CreateEntryAsync(CreateTimeEntryRequest request) =>
        Tracker is null ? api!.CreateTimeEntryAsync(workspace!.Id, request) : Tracker.CreateAsync(workspace!.Id, request);

    public Task<TimeEntryResponse> UpdateEntryAsync(TimeEntryResponse current, TimeEntryRequest request) =>
        Tracker is null ? api!.UpdateTimeEntryAsync(current.WorkspaceId, current, request) : Tracker.UpdateAsync(current, request);

    public Task DeleteEntryAsync(TimeEntryResponse entry) =>
        Tracker is null ? api!.DeleteTimeEntryAsync(entry.WorkspaceId, entry) : Tracker.DeleteAsync(entry);

    /// <summary>Starts a timer; a running one is stopped first, as the server would.</summary>
    public async Task StartAsync(CreateTimeEntryRequest request)
    {
        if (ReadOnlyOrganization is not null)
        {
            Notify("This workspace is read-only, so time can't be added or changed.");
            return;
        }
        await MutateAsync(async () =>
        {
            if (Running is { } current) await StopEntryAsync(current, request.Start ?? DateTimeOffset.UtcNow);
            await CreateEntryAsync(request with { Id = request.Id ?? Guid.NewGuid(), Start = request.Start ?? DateTimeOffset.UtcNow });
        });
    }

    private Task StopEntryAsync(TimeEntryResponse entry, DateTimeOffset at) =>
        Tracker is null ? api!.StopTimeEntryAsync(entry.WorkspaceId, entry.Id, at) : Tracker.StopAsync(entry, at);

    /// <summary>Stops the running timer; when the workspace needs details it lacks, the editor asks for them first.</summary>
    public async Task StopAsync(DateTimeOffset? at = null)
    {
        if (Running is not { } current) return;
        var missing = Rules.Missing(current.Description, current.ProjectId, current.TaskId, current.TagIds, current.IsSplit(), Catalog.Project(current.ProjectId), SplitMath.DescribesParts(current));
        if (missing.Count > 0 || Rules.MissingInParts(current.IsSplit() ? current.Parts() : [], Catalog).Count > 0)
        {
            await EditAsync(current, stopping: true);
            return;
        }
        await MutateAsync(() => StopEntryAsync(current, at ?? DateTimeOffset.UtcNow));
        Stopped?.Invoke(current);
    }

    /// <summary>An entry the timer just stopped, e.g. to offer splitting it.</summary>
    public event Action<TimeEntryResponse>? Stopped;

    public async Task ContinueAsync(TimeEntryResponse entry) => await StartAsync(entry.Continuing(DateTimeOffset.UtcNow));

    /// <summary>Changes the running entry, e.g. its project from the timer bar.</summary>
    public async Task UpdateRunningAsync(Func<TimeEntryRequest, TimeEntryRequest> change)
    {
        if (Running is not { } running) return;
        await MutateAsync(() => UpdateEntryAsync(running, change(running.Request())));
    }

    public async Task ToggleTimerAsync()
    {
        if (api is null || workspace is null)
        {
            Show();
            Activate();
            return;
        }
        if (Running is not null) await StopAsync();
        else if (shell?.TimerView is { } timer) await timer.StartAsync();
        else await StartAsync(new CreateTimeEntryRequest());
    }

    /// <summary>Opens the entry editor over the window: an entry, a new one, or the running one being stopped.</summary>
    public async Task<bool> EditAsync(TimeEntryResponse? entry, DateTimeOffset? initial = null, DateTimeOffset? initialEnd = null, bool stopping = false, EditorStart? start = null) =>
        (await EditorAsync(entry, initial, initialEnd, stopping, start))?.Saved == true;

    /// <summary>Opens the editor; returns the entry saved as one entry, or null when it wasn't (or was saved as several).</summary>
    public async Task<Guid?> EditEntryAsync(TimeEntryResponse? entry, DateTimeOffset? initial = null, DateTimeOffset? initialEnd = null, EditorStart? start = null) =>
        (await EditorAsync(entry, initial, initialEnd, false, start)) is { Saved: true } editor ? editor.SavedEntryId : null;

    private async Task<EntryEditor?> EditorAsync(TimeEntryResponse? entry, DateTimeOffset? initial, DateTimeOffset? initialEnd, bool stopping, EditorStart? start)
    {
        if (api is null || workspace is null) return null;
        var editor = new EntryEditor(this, entry, initial, initialEnd, stopping, start);
        await Dialogs.ShowAsync(editor, 640);
        if (editor.Saved)
        {
            if (stopping && entry is not null) Stopped?.Invoke(entry);
            await RefreshSafelyAsync();
            RefreshViews();
        }
        return editor;
    }

    // Agents' proposals

    /// <summary>
    /// Tells of proposals an agent just made: a notice in the window, and a notification while the person is in another
    /// app, e.g. the terminal they asked from.
    /// </summary>
    private void ProposalsArrived(int count, string? client)
    {
        var summary = $"{client ?? "An agent"} proposed {(count == 1 ? "a change" : $"{count} changes")} to your time.";
        Notify(summary, NoticeKind.Success);
        if (!IsActive) Toasts.Show(ToastKind.Proposals, summary, "Nothing is saved until you add it. Review them in Spindle.", [("Review", ReviewProposalsAsync)]);
    }

    /// <summary>
    /// Opens the review of what agents proposed. Editing a proposal closes it for the editor, and it opens again after,
    /// as long as anything waits or was saved in it.
    /// </summary>
    public async Task ReviewProposalsAsync()
    {
        if (reviewing || workspace is null) return;
        Toasts.Close(ToastKind.Proposals);
        Show();
        Activate();
        reviewing = true;
        try
        {
            try
            {
                await Proposals.LoadAsync();
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or ApiException)
            {
                // Offline: they are checked against what this PC has.
            }
            while (await Dialogs.ShowAsync(new ProposalReview(this), 820) is AgentProposal editing)
            {
                var current = Proposals.Entry(editing.EntryId);
                if (!editing.IsNew && current is null) continue;
                var request = Proposals.Request(editing);
                if (await EditEntryAsync(current, request.Start, request.Stop ?? request.Start, start: new EditorStart(Proposed: request)) is { } saved)
                {
                    try
                    {
                        await Proposals.SavedInEditorAsync(editing, saved);
                    }
                    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or ApiException)
                    {
                        Notify(ex is ApiException ? ex.Message : "Couldn't answer the proposal.");
                    }
                }
            }
        }
        finally
        {
            reviewing = false;
            Proposals.EndReview();
            RefreshViews();
        }
    }

    // The day's review

    /// <summary>
    /// Opens the review of these days. Change… closes it for the editor, and it opens again after, on the days it was
    /// showing.
    /// </summary>
    public async Task OpenReviewAsync(ReviewScope scope)
    {
        if (reviewingDays || workspace is null) return;
        Toasts.Close(ToastKind.DayReview);
        Show();
        WindowState = WindowState.Normal;
        Activate();
        if (Section is not (Section.Timer or Section.Calendar)) await NavigateAsync(Section.Timer);
        reviewingDays = true;
        try
        {
            var current = scope;
            // What was dealt with stays in its place, with Undo, until the review closes.
            var session = new ReviewSession();
            while (await Dialogs.ShowAsync(new DayReviewView(this, current, session), 860) is DayReviewView.Changing changing)
            {
                if (await Desktop.ChangeAsync(changing.Item))
                {
                    session.Mark(changing.Item, ReviewOutcome.Changed, "Saved in the editor");
                    session.Notice = null;
                }
                current = changing.Scope;
            }
        }
        finally
        {
            reviewingDays = false;
            RefreshViews();
        }
    }

    /// <summary>What agent logs found in the last week, right after they were turned on, or from Settings.</summary>
    public async Task ShowLogsFoundAsync()
    {
        if (workspace is null || !AgentLogsEnabled) return;
        Show();
        Activate();
        if (await Dialogs.ShowAsync(new AgentLogsOnboarding(this), 620) is ReviewScope review) await OpenReviewAsync(review);
    }

    // Catalog changes from pickers

    public async Task<ProjectResponse> CreateProjectAsync(string name)
    {
        var project = await api!.CreateProjectAsync(WorkspaceId, new ProjectRequest { Name = name, Color = Catalog.NextColor, IsBillable = false });
        Catalog = new Catalog([.. Catalog.Projects, project], Catalog.Clients, Catalog.Tags);
        return project;
    }

    public async Task<TagResponse> CreateTagAsync(string name)
    {
        var tag = await api!.CreateTagAsync(WorkspaceId, new TagRequest { Name = name });
        Catalog = new Catalog(Catalog.Projects, Catalog.Clients, [.. Catalog.Tags, tag]);
        return tag;
    }

    /// <summary>Takes a fresh copy of the catalog after the catalog pages changed it.</summary>
    public async Task ReloadCatalogAsync()
    {
        if (api is null || workspace is null) return;
        var projects = api.GetProjectsAsync(workspace.Id, true);
        var clients = api.GetClientsAsync(workspace.Id, true);
        var tags = api.GetTagsAsync(workspace.Id);
        await Task.WhenAll(projects, clients, tags);
        Catalog = new Catalog(await projects, await clients, await tags);
    }

    public async Task ReloadAccountAsync()
    {
        if (api is null) return;
        me = await api.GetMeAsync();
        Time = UserTime.For(me);
        RefreshViews();
    }

    // Signing out

    internal async Task SignOutAsync()
    {
        if (Running is not null && !await ConfirmAsync("A timer is running", "Signing out leaves the timer running on the server. Sign out anyway?", "Sign out")) return;
        if (PendingCount > 0 && !await ConfirmAsync("Unsynced changes", "Changes waiting to sync will stay on this PC for this account. Sign out anyway?", "Sign out")) return;
        if (liveSync is not null)
        {
            await liveSync.DisposeAsync();
            liveSync = null;
        }
        sessionGeneration++;
        desktopCache?.Clear();
        try
        {
            await api!.LogoutAsync();
        }
        finally
        {
            Proposals.Clear();
            me = null;
            workspace = null;
            Running = null;
            Tracker = null;
            Activity.Clear();
            ActivitySync.Forget();
            Catalog = Catalog.Empty;
            Entries = [];
            pulse.Stop();
            Desktop.Stop();
            Desktop.Withdraw();
            Splits.Clear();
            shortcut?.Dispose();
            TimerChanged?.Invoke();
            ShowSignIn();
        }
    }

    // Messages

    public Task<bool> ConfirmAsync(string title, string message, string confirm = "Confirm", bool destructive = false) =>
        Dialogs.ConfirmAsync(title, message, confirm, destructive);

    /// <summary>Shows a short notice at the foot of the window: what went wrong, or what was done.</summary>
    public void Notify(string message) => Notify(message, NoticeKind.Error);

    public void Notify(string message, NoticeKind kind) => Notices.Show(message, kind);

    public void OpenWeb(string path, Uri? server = null)
    {
        var uri = new Uri(server ?? api?.ServerUri ?? ServerUri(preferences.ServerUrl), path);
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }

    public void Quit()
    {
        quitting = true;
        Close();
    }
}
