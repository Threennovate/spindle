using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using Spindle.Activity;
using Spindle.Core;
using Spindle.Platform;
using Spindle.Windows.Model;
using Spindle.Windows.Views;
using Spindle.Windows.Views.Shell;

namespace Spindle.Windows.Desktop;

/// <summary>A split offered for an entry the timer just stopped, or a switch for the running one.</summary>
public sealed record SplitOffer(TimeEntryResponse Entry, SplitSuggestion Suggestion);

/// <summary>
/// What Spindle does on its own while it runs, as the Mac's Desktop: asks about time away from a running timer,
/// reminds to track in working hours, keeps the Pomodoro, and offers splits when agents worked on several projects.
/// Every five seconds; nothing changes time without the person's click.
/// </summary>
public sealed class DesktopService
{
    private readonly MainWindow owner;
    private readonly Func<TimeSpan?> idle;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(5) };
    private Guid? lastRunning;
    private DateTimeOffset? lastStop;
    private DateTimeOffset? lastReminder;
    private DateTimeOffset? awaySince;
    private bool asking;
    private string? lastInterval;
    private TimeEntryResponse? pausedEntry;
    private readonly HashSet<Guid> switchesOffered = [];
    private readonly HashSet<object> undone = [];
    /// <summary>
    /// The agents' untracked work the last reminder named, for its "Start from 9:12" button; the next stretch of it may
    /// be named without waiting for the next reminder.
    /// </summary>
    private (Guid ProjectId, DateTimeOffset Since)? remindedOf;
    /// <summary>When the day's review was last looked for at the end of working hours.</summary>
    private DateTimeOffset dayReviewChecked = DateTimeOffset.MinValue;

    public DesktopService(MainWindow owner, Func<TimeSpan?>? idle = null)
    {
        this.owner = owner;
        this.idle = idle ?? (() => OperatingSystem.IsWindows() ? WindowsDesktop.GetIdleDuration() : null);
        timer.Tick += async (_, _) => await TickAsync();
        owner.Stopped += entry => _ = OfferAfterStopAsync(entry);
    }

    /// <summary>The split offered after the last stop, shown at the foot of the window.</summary>
    public SplitOffer? Offer { get; private set; }

    /// <summary>
    /// For a split entry the timer just stopped, ending its split where the work changed, when nobody answered that
    /// while it ran. Shown at the foot of the window.
    /// </summary>
    public DriftOffer? DriftOffer { get; private set; }
    public event Action? OfferChanged;

    public void Start() => timer.Start();
    public void Stop() => timer.Stop();

    public async Task TickAsync(DateTimeOffset? at = null)
    {
        var now = at ?? DateTimeOffset.UtcNow;
        if (owner.Me is null) return;
        RunningChanged(now);
        await IdleAsync(now);
        await RemindersAsync(now);
        await PomodoroAsync(now);
        await ChangeOfferAsync(now);
        await OfferDayReviewAsync(now);
        await owner.ActivitySync.TickAsync(now);
    }

    private void RunningChanged(DateTimeOffset now)
    {
        var running = owner.Running?.Id;
        if (running == lastRunning) return;
        if (lastRunning is not null && running is null) lastStop = now;
        lastRunning = running;
        if (running is null) return;
        Toasts.Close(ToastKind.Reminder);
        // Starting a timer ends a break.
        owner.BreakEnds = null;
        pausedEntry = null;
    }

    // Time away

    private async Task IdleAsync(DateTimeOffset now)
    {
        var preferences = owner.Preferences;
        if (owner.Running is not { } running || !preferences.DetectIdle)
        {
            awaySince = null;
            return;
        }
        if (idle() is not { } away) return;
        if (awaySince is null && away.TotalMinutes >= preferences.IdleMinutes)
        {
            var since = now - away;
            awaySince = since > running.Start ? since : running.Start;
        }
        else if (awaySince is { } since && away.TotalSeconds < 5)
        {
            awaySince = null;
            await AskAboutTimeAwayAsync(running, since, now);
        }
    }

    /// <summary>Asks what happens to the time away: keep it, drop it, or drop it and go on with the same entry.</summary>
    public async Task AskAboutTimeAwayAsync(TimeEntryResponse running, DateTimeOffset since, DateTimeOffset now)
    {
        if (asking) return;
        asking = true;
        try
        {
            owner.Show();
            owner.WindowState = WindowState.Normal;
            owner.Activate();
            var minutes = (int)(now - since).TotalMinutes;
            var length = minutes >= 60 ? $"{minutes / 60}h {minutes % 60:00}m" : $"{minutes}m";
            var what = running.Description.Length == 0 ? "No description" : running.Description;
            var keep = Ui.Button("Keep it", () => owner.Dialogs.Close("keep"), "primary").Named("KeepAway");
            var discard = Ui.Button("Discard it", () => owner.Dialogs.Close("discard")).Named("DiscardAway");
            var resume = Ui.Button("Discard it and continue", () => owner.Dialogs.Close("continue")).Named("ContinueAway");
            var buttons = Ui.Row(8, resume, discard, keep);
            buttons.HorizontalAlignment = HorizontalAlignment.Right;
            var body = Ui.Stack(12, Ui.Text($"You were away for {length}", "subtitle"), Ui.Wrapped($"Since {owner.Time.Clock(since)}, while “{what}” was running. What should happen to that time?"));
            body.Margin = new Thickness(24);
            var footer = new Border { Child = buttons, Padding = new Thickness(24, 16) }.Res(Border.BackgroundProperty, "CardBackgroundSecondaryBrush");
            var answer = await owner.Dialogs.ShowAsync(new DockPanel { Children = { footer.Dock(Dock.Bottom), body } }, 520);
            if (owner.Running?.Id != running.Id || answer is not ("discard" or "continue")) return;
            await owner.MutateAsync(() => owner.UpdateEntryAsync(owner.Running!, owner.Running!.Request() with { Stop = since }));
            if (answer is "continue") await owner.StartAsync(running.Continuing(DateTimeOffset.UtcNow));
        }
        catch (Exception exception)
        {
            owner.Notify(exception.Message);
        }
        finally
        {
            asking = false;
        }
    }

    // Reminders

    private async Task RemindersAsync(DateTimeOffset now)
    {
        var preferences = owner.Preferences;
        if (!preferences.RemindToTrack || owner.BreakEnds is { } ends && ends > now) return;
        var schedule = new ReminderSchedule(preferences.ReminderDays, preferences.ReminderFrom, preferences.ReminderTo, preferences.ReminderEveryMinutes);
        // Agents at work in a linked project with no timer make the reminder concrete, as soon as they have been at it
        // for a while, once for each stretch of their work.
        (Guid ProjectId, DateTimeOffset Since)? work = null;
        if (owner.Running is null && owner.AgentLogsEnabled && schedule.IsWorkingTime(now, owner.Time))
        {
            await owner.Splits.LoadAsync([owner.Time.Day(now)]);
            if (owner.Splits.OngoingUntracked(now) is { Projects.Count: > 0 } ongoing)
                work = (ongoing.Projects[0], DateTimeOffset.FromUnixTimeMilliseconds(ongoing.Range.Start));
        }
        var fresh = work is not null && work.Value.Since != remindedOf?.Since;
        if (!schedule.ShouldRemind(now, owner.Time, owner.Running is not null, idle()?.TotalSeconds ?? 0, fresh ? null : lastReminder, fresh ? null : lastStop)) return;
        lastReminder = now;
        remindedOf = work;
        if (work is { } named)
        {
            var name = owner.Catalog.Project(named.ProjectId)?.Name ?? "a project";
            var clock = owner.Time.Clock(named.Since);
            // "Not now" only answers this notification; the next reminder comes as usual.
            Toasts.Show(ToastKind.Reminder, $"Agents have been working in {name} since {clock}", $"Start {name} from {clock}?",
                [($"Start from {clock}", StartRemindedWorkAsync), ("Not now", () => Task.CompletedTask)], owner.FocusNewEntryAsync);
            return;
        }
        Toasts.Show(ToastKind.Reminder, "Are you tracking your time?", "Nothing is running. Start a timer to keep your day complete.",
            [("Start timer", () => owner.StartAsync(new CreateTimeEntryRequest()))], owner.FocusNewEntryAsync);
    }

    /// <summary>Starts the timer on the project agents have been working in, from when they began, as the reminder offered.</summary>
    public async Task StartRemindedWorkAsync()
    {
        if (owner.Running is not null || remindedOf is not { } work) return;
        remindedOf = null;
        if (owner.ReadOnlyOrganization is not null || owner.Rules.IsLocked(owner.Time.Day(work.Since)))
        {
            await owner.StartAsync(new CreateTimeEntryRequest());
            return;
        }
        var id = Guid.NewGuid();
        var name = owner.Catalog.Project(work.ProjectId)?.Name ?? "the project";
        await owner.StartAsync(new CreateTimeEntryRequest { Id = id, ProjectId = work.ProjectId, IsBillable = owner.Catalog.Project(work.ProjectId)?.IsBillable ?? false, Start = work.Since });
        Saved($"Started {name} from {owner.Time.Clock(work.Since)}", [new SavedChange(null, [id])]);
    }

    // Pomodoro

    private async Task PomodoroAsync(DateTimeOffset now)
    {
        var preferences = owner.Me?.Preferences;
        if (preferences is null || !preferences.PomodoroEnabled)
        {
            owner.BreakEnds = null;
            return;
        }
        if (owner.BreakEnds is { } ends && now >= ends)
        {
            owner.BreakEnds = null;
            var paused = pausedEntry;
            pausedEntry = null;
            Toasts.Show(ToastKind.BreakOver, "Break's over",
                paused is null ? "Ready for the next focus interval?" : $"Ready to continue “{(paused.Description.Length == 0 ? "No description" : paused.Description)}”?",
                [("Continue", () => paused is null ? owner.StartAsync(new CreateTimeEntryRequest()) : owner.ContinueAsync(paused))], owner.FocusNewEntryAsync);
        }
        if (owner.Running is not { } running) return;
        var intervals = Pomodoro.Intervals((now - running.Start).TotalSeconds, preferences.PomodoroMinutes);
        if (intervals <= 0) return;
        var key = $"{running.Id}:{intervals}";
        if (key == lastInterval) return;
        lastInterval = key;
        var focus = preferences.PomodoroMinutes;
        var rest = preferences.PomodoroBreakMinutes;
        if (preferences.PomodoroAutoStop)
        {
            pausedEntry = running;
            owner.BreakEnds = now.AddMinutes(rest);
            var end = running.Start.AddMinutes(intervals * focus);
            await owner.StopAsync(end < now ? end : now);
            Toasts.Show(ToastKind.FocusDone, "Time for a break", $"{focus} minutes of focus done. The timer stopped; take {rest} minutes off.", []);
        }
        else
        {
            Toasts.Show(ToastKind.FocusDone, "Time for a break", $"{focus} minutes of focus done. Take {rest} minutes off.", [("Stop timer", () => owner.StopAsync())]);
        }
    }

    // Splits

    /// <summary>Offers a split for an entry the timer just stopped, when its time went to several projects.</summary>
    public async Task OfferAfterStopAsync(TimeEntryResponse stopped)
    {
        if (!owner.AgentLogsEnabled) return;
        TimeEntryResponse? entry = null;
        for (var attempt = 0; attempt < 20 && entry is null; attempt++)
        {
            entry = owner.Entries.FirstOrDefault(e => e.Id == stopped.Id && e.Stop is not null);
            if (entry is null) await Task.Delay(250);
        }
        if (entry is null) return;
        await owner.Splits.LoadAsync([owner.Time.Day(entry.Start), owner.Time.Day(entry.Stop!.Value)]);
        // A split entry gets only the change nobody answered while it ran.
        if (entry.IsSplit())
        {
            if (owner.Splits.Drift(entry) is not { } drift) return;
            DriftOffer = drift;
            OfferChanged?.Invoke();
            DispatcherTimer.RunOnce(() =>
            {
                if (DriftOffer == drift) Withdraw();
            }, TimeSpan.FromSeconds(45));
            if (!owner.IsActive && owner.Preferences.NotifySwitches) NotifyDrift(drift);
            return;
        }
        // Repositories without a project are left to the entry's chip, where they can be linked, and so is a suggestion
        // the logs back only weakly: it waits quietly instead of interrupting.
        if (owner.Splits.Suggestion(entry) is not { Strength: SplitStrength.Strong } suggestion || suggestion.Parts.Any(p => p.Target.ProjectId is null)) return;
        Offer = new SplitOffer(entry, suggestion);
        OfferChanged?.Invoke();
        var offered = Offer;
        DispatcherTimer.RunOnce(() =>
        {
            if (Offer == offered) Withdraw();
        }, TimeSpan.FromSeconds(45));
        if (!owner.IsActive && owner.Preferences.NotifySwitches)
        {
            Toasts.Show(ToastKind.SplitOffer, entry.Description.Length == 0 ? "Split the time?" : $"Split “{entry.Description}”?",
                $"Your agents worked on more than one project: {owner.Splits.Summary(suggestion, entry)}.",
                [(owner.Splits.Title(offered.Suggestion), () => TakeAsync(offered)), ("Keep as is", () => KeepAsync(offered))], () => ReviewAsync(offered));
        }
    }

    public void Withdraw()
    {
        Offer = null;
        DriftOffer = null;
        OfferChanged?.Invoke();
        Toasts.Close(ToastKind.SplitOffer);
    }

    public async Task TakeAsync(SplitOffer offer)
    {
        Withdraw();
        var entry = owner.Entries.FirstOrDefault(e => e.Id == offer.Entry.Id);
        if (entry is null || entry.IsSplit() || entry.ProjectId != offer.Entry.ProjectId) return;
        if (owner.Rules.Missing(entry.Description, entry.ProjectId, entry.TaskId, entry.TagIds, false, owner.Catalog.Project(entry.ProjectId)).Count > 0)
        {
            await owner.Splits.ReviewAsync(entry);
            return;
        }
        try
        {
            Saved(await owner.Splits.ApplyAsync(entry, offer.Suggestion));
        }
        catch (Exception exception)
        {
            owner.Notify(exception.Message.Length > 0 ? exception.Message : "Couldn't split the entry.");
        }
    }

    /// <summary>
    /// Says what one click just saved, with Undo: a notice in the window, and a notification while the window isn't in
    /// front, e.g. after a take from the tray or a notification.
    /// </summary>
    private void Saved(SplitTaken taken) => Saved(taken.Notice, [taken.Change]);

    /// <summary>One change, or all that "Take all" saved, with one Undo for them.</summary>
    private void Saved(string notice, IReadOnlyList<SavedChange> changes)
    {
        owner.Notices.Show(notice, NoticeKind.Success, ("Undo", () => UndoAsync(changes)));
        if (!owner.IsActive)
            Toasts.Show(ToastKind.SplitSaved, notice, "Undo puts the entry back as it was.", [("Undo", () => UndoAsync(changes))]);
    }

    /// <summary>Puts back what a one-click take changed. Once is enough, from the notice or the notification.</summary>
    public Task UndoAsync(SavedChange change) => UndoAsync([change]);

    private async Task UndoAsync(IReadOnlyList<SavedChange> changes)
    {
        Toasts.Close(ToastKind.SplitSaved);
        owner.Notices.Hide();
        if (!undone.Add(changes)) return;
        try
        {
            // The newest first, so each is put back over what came after it.
            foreach (var change in changes.Reverse()) await owner.Splits.UndoAsync(change);
            owner.Notify("Undone", NoticeKind.Success);
        }
        catch (Exception exception)
        {
            owner.Notify(exception.Message.Length > 0 ? exception.Message : "Couldn't undo.");
        }
    }

    public async Task KeepAsync(SplitOffer offer)
    {
        Withdraw();
        await owner.Splits.DismissAsync(offer.Entry);
    }

    public async Task ReviewAsync(SplitOffer offer)
    {
        Withdraw();
        owner.Show();
        owner.Activate();
        await owner.Splits.ReviewAsync(offer.Entry);
    }

    /// <summary>
    /// The work changed while the timer runs: for an entry on one project, it moved to another one, which has had the
    /// agents' attention for 15 minutes, and a notification offers to switch the timer from then, once per entry. For a
    /// split, one of its projects went quiet or another joined, offered once per split.
    /// </summary>
    private async Task ChangeOfferAsync(DateTimeOffset now)
    {
        if (!owner.AgentLogsEnabled || owner.Running is not { } running) return;
        // Notes when the running entry's split is set, also when it is split only later.
        owner.Splits.Statement(running, now);
        if (!owner.Preferences.NotifySwitches) return;
        if (running.IsSplit())
        {
            await owner.Splits.LoadAsync([owner.Time.Day(now)]);
            if (owner.Running?.Id != running.Id || owner.Splits.Drift(running, now) is not { } drift
                || owner.Preferences.SplitStatements.GetValueOrDefault(running.Id)?.Notified == true) return;
            owner.Splits.MarkNotified(drift);
            NotifyDrift(drift);
            return;
        }
        if (switchesOffered.Contains(running.Id)) return;
        await owner.Splits.LoadAsync([owner.Time.Day(now)]);
        if (owner.Splits.Suggestion(running) is not { Stretches: { Count: 2 } stretches } suggestion) return;
        if (stretches[0].Target.ProjectId != running.ProjectId || stretches[1].Target.ProjectId is not { } target) return;
        var since = DateTimeOffset.FromUnixTimeMilliseconds(stretches[1].Start);
        if (now - since < TimeSpan.FromMinutes(15)) return;
        switchesOffered.Add(running.Id);
        var from = owner.Catalog.Project(running.ProjectId)?.Name ?? "this project";
        var to = owner.Catalog.Project(target)?.Name ?? "another project";
        var offer = new SplitOffer(running, suggestion);
        // "Still on Atlas" only answers this question: the switch was offered, and the split can still come when the
        // timer stops.
        Toasts.Show(ToastKind.SwitchOffer, $"Still on {from}?", $"Your agents have been working in {to} since {owner.Time.Clock(since)}. Switch the timer to {to} from then?",
            [($"Switch to {to}", () => TakeAsync(offer)), ($"Still on {from}", () => Task.CompletedTask)], () => ReviewAsync(offer));
    }

    private void NotifyDrift(DriftOffer offer) =>
        Toasts.Show(ToastKind.SwitchOffer, owner.Splits.Headline(offer.Drift), owner.Splits.Question(offer),
            [(owner.Splits.Title(offer.Drift), () => TakeAsync(offer)), ("Not now", () => NotNowAsync(offer))], () => ReviewAsync(offer));

    /// <summary>Ends a split entry where its work changed and continues in a new entry, with one click.</summary>
    public async Task TakeAsync(DriftOffer offer)
    {
        Withdraw();
        Toasts.Close(ToastKind.SwitchOffer);
        owner.Splits.Answer(offer);
        var entry = owner.Entries.FirstOrDefault(e => e.Id == offer.Entry.Id);
        if (entry is null || SplitAdvisor.PartsKey(entry.Parts()) != offer.Statement) return;
        // The entry ends at the change, so it needs what a finished entry needs; the editor asks for it.
        if (owner.Rules.Missing(entry.Description, entry.ProjectId, entry.TaskId, entry.TagIds, true, owner.Catalog.Project(entry.ProjectId), SplitMath.DescribesParts(entry)).Count > 0)
        {
            await owner.EditAsync(entry);
            return;
        }
        try
        {
            Saved(await owner.Splits.EndAndContinueAsync(entry, offer.Drift));
        }
        catch (Exception exception)
        {
            owner.Notify(exception.Message.Length > 0 ? exception.Message : "Couldn't change the entry.");
        }
    }

    /// <summary>Leaves the split as it is; nothing more is offered until it changes.</summary>
    public Task NotNowAsync(DriftOffer offer)
    {
        Withdraw();
        Toasts.Close(ToastKind.SwitchOffer);
        owner.Splits.Answer(offer);
        return Task.CompletedTask;
    }

    /// <summary>Opens the entry, to change its split or times there.</summary>
    public async Task ReviewAsync(DriftOffer offer)
    {
        await NotNowAsync(offer);
        owner.Show();
        owner.Activate();
        if (owner.Entries.FirstOrDefault(e => e.Id == offer.Entry.Id) is { } entry) await owner.EditAsync(entry);
    }

    // Time nobody tracked

    /// <summary>
    /// Moves the running entry's start back to where the agents' untracked work began. A project the person chose stays;
    /// a timer without one gets the agents' project too.
    /// </summary>
    public async Task TakeAsync(EarlierStart offer)
    {
        if (owner.Running is not { } running || running.Id != offer.Entry.Id || running.Start != offer.Entry.Start || owner.Rules.IsLocked(owner.Time.Day(offer.Start))) return;
        try
        {
            var project = owner.Catalog.Project(offer.FillsProjectId);
            if (project is not null && running.ProjectId is null)
                await owner.UpdateRunningAsync(r => r with { Start = offer.Start, ProjectId = project.Id, TaskId = null, IsBillable = project.IsBillable, Shares = [] });
            else
                await owner.UpdateRunningAsync(r => r with { Start = offer.Start });
            var started = project is not null && running.ProjectId is null ? $"Started {project.Name} at" : "Started at";
            Saved($"{started} {owner.Time.Clock(offer.Start)}", [new SavedChange(running, [])]);
        }
        catch (Exception exception)
        {
            owner.Notify(exception.Message.Length > 0 ? exception.Message : "Couldn't change the start.");
        }
    }

    // The day's review

    /// <summary>What the review offers on these days, with the time agents proposed entries for spoken for, as in the calendar.</summary>
    public List<ReviewItem> Review(IEnumerable<DateOnly> days) => owner.Splits.Review(days, Proposed());

    /// <summary>Untracked time on these days, without the time agents proposed entries for.</summary>
    public List<UntrackedBlock> Untracked(IEnumerable<DateOnly> days) => owner.Splits.Untracked(days, Proposed());

    private List<TimeRange> Proposed() => owner.Proposals.Pending.Select(owner.Proposals.Request)
        .Where(r => r.Start is not null && r.Stop is not null)
        .Select(r => new TimeRange(r.Start!.Value.ToUnixTimeMilliseconds(), Math.Max(r.Start.Value.ToUnixTimeMilliseconds(), r.Stop!.Value.ToUnixTimeMilliseconds())))
        .ToList();

    /// <summary>Reads what the review of these days needs: their entries as the server has them, and their agent activity.</summary>
    public async Task LoadReviewAsync(IReadOnlyList<DateOnly> days)
    {
        if (days.Count == 0) return;
        // A day before too, for an entry that runs over midnight into the first.
        await owner.LoadEntriesAsync(owner.Time.StartOf(days.Min().AddDays(-1)), owner.Time.StartOf(days.Max().AddDays(1)));
        await owner.Splits.LoadAsync(days);
    }

    /// <summary>
    /// Takes one item of the review with one click, with Undo. When the workspace needs details one click can't fill in,
    /// the editor opens with it instead (<see cref="ChangeAsync"/>): the caller is told so with false.
    /// </summary>
    public async Task<bool> TakeAsync(ReviewItem item)
    {
        try
        {
            switch (await ApplyAsync(item))
            {
                case { } change:
                    Saved(Taken(item), [change]);
                    return true;
                case null when NeedsDetails(item):
                    return false;
            }
        }
        catch (Exception exception)
        {
            owner.Notify(exception.Message.Length > 0 ? exception.Message : "Couldn't save it.");
        }
        return true;
    }

    /// <summary>
    /// Puts back what takes in the review changed, the newest first, so each is put back over what came after it. The
    /// review says what happened itself.
    /// </summary>
    public async Task UndoTakesAsync(IReadOnlyList<SavedChange> changes)
    {
        if (!undone.Add(changes)) return;
        foreach (var change in changes.Reverse()) await owner.Splits.UndoAsync(change);
    }

    /// <summary>
    /// Opens an item of the review in the editor with what it offers filled in, to change before saving. True when it
    /// was saved there.
    /// </summary>
    public async Task<bool> ChangeAsync(ReviewItem item)
    {
        switch (item)
        {
            case ReviewItem.Split split when owner.Entries.FirstOrDefault(e => e.Id == split.Offer.Entry.Id) is { } entry:
                var take = owner.Splits.Take(split.Offer.Suggestion, entry.TaskId, entry.IsBillable, entry.ProjectId);
                return await owner.EditAsync(entry, start: new EditorStart(Split: take?.Stretches is null ? take?.Parts : null, Stretches: take?.Stretches));
            case ReviewItem.Drift drift when owner.Entries.FirstOrDefault(e => e.Id == drift.Offer.Entry.Id) is { } entry:
                return await owner.EditAsync(entry);
            case ReviewItem.Untracked untracked:
                return await owner.Splits.EditAsync(untracked.Block);
        }
        return false;
    }

    /// <summary>
    /// Why one click can't take an item, for its row, or null when it can: a repository without a project, or details
    /// the workspace requires that a suggestion can't fill in. <see cref="ReviewHold.Reason"/> is null where the row
    /// says it otherwise, e.g. with "Choose a project for atlas".
    /// </summary>
    public ReviewHold? Hold(ReviewItem item)
    {
        switch (item)
        {
            case ReviewItem.Split split when split.Offer.Suggestion.Parts.Any(p => p.Target.ProjectId is null):
                return new ReviewHold(null);
            case ReviewItem.Untracked untracked when untracked.Block.Stretch.Projects.Count == 0:
                return new ReviewHold("In repositories without a project. Change… to choose one.");
        }
        return Needs(item) is { } needs ? new ReviewHold($"{needs}. Change… to add it.") : null;
    }

    /// <summary>What the workspace requires that one click can't fill in for an item, e.g. "Needs a task on Beacon".</summary>
    private string? Needs(ReviewItem item)
    {
        var splits = owner.Splits;
        switch (item)
        {
            case ReviewItem.Untracked untracked:
                return splits.Needs(untracked.Block);
            case ReviewItem.Drift drift when owner.Entries.FirstOrDefault(e => e.Id == drift.Offer.Entry.Id) is { } entry:
                return splits.Needs(entry.Description, entry.ProjectId, entry.TaskId, entry.TagIds, entry.Parts(), SplitMath.DescribesParts(entry));
            case ReviewItem.Split split when owner.Entries.FirstOrDefault(e => e.Id == split.Offer.Entry.Id) is { } entry:
            {
                if (splits.Take(split.Offer.Suggestion, entry.TaskId, entry.IsBillable, entry.ProjectId) is not { } take) return null;
                // Separate entries each get the entry's description and tags, on their project with its task.
                if (take.Stretches is { Length: > 1 } stretches)
                    return stretches.Select(s => s.ProjectId).Distinct()
                        .Select(id => splits.Needs(entry.Description, id, take.Parts?.FirstOrDefault(p => p.ProjectId == id)?.TaskId, entry.TagIds, null))
                        .FirstOrDefault(n => n is not null);
                return splits.Needs(entry.Description, entry.ProjectId, entry.TaskId, entry.TagIds, take.Parts);
            }
        }
        return null;
    }

    /// <summary>Whether one click can't save it because the workspace needs details, so the editor is the way.</summary>
    private bool NeedsDetails(ReviewItem item) => item is ReviewItem.Untracked { Block.Stretch.Projects.Count: 0 } || Needs(item) is not null;

    /// <summary>
    /// Saves what a review item offers, on the entry as it is now. Null when the entry changed meanwhile, so the item no
    /// longer applies, or when it needs details first.
    /// </summary>
    public async Task<SavedChange?> ApplyAsync(ReviewItem item)
    {
        if (Hold(item) is not null) return null;
        switch (item)
        {
            case ReviewItem.Split split:
            {
                var offer = split.Offer;
                if (owner.Entries.FirstOrDefault(e => e.Id == offer.Entry.Id) is not { } entry || entry.IsSplit() || entry.ProjectId != offer.Entry.ProjectId
                    || entry.Start != offer.Entry.Start || entry.Stop != offer.Entry.Stop) return null;
                return (await owner.Splits.ApplyAsync(entry, offer.Suggestion)).Change;
            }
            case ReviewItem.Drift drift:
            {
                var offer = drift.Offer;
                if (owner.Entries.FirstOrDefault(e => e.Id == offer.Entry.Id) is not { } entry || SplitAdvisor.PartsKey(entry.Parts()) != offer.Statement) return null;
                var taken = await owner.Splits.EndAndContinueAsync(entry, offer.Drift);
                owner.Splits.Answer(offer);
                return taken.Change;
            }
            case ReviewItem.Untracked untracked:
            {
                if (owner.Splits.Requests(untracked.Block) is not { } requests) return null;
                await owner.MutateAsync(async () =>
                {
                    foreach (var request in requests) await owner.CreateEntryAsync(request);
                });
                return new SavedChange(null, requests.Select(r => r.Id!.Value).ToList());
            }
        }
        return null;
    }

    /// <summary>What one click on an item did, for its notice: "Split saved", "Split ended at 11:05", "Added Atlas 14:00–15:30".</summary>
    public string Taken(ReviewItem item) => item switch
    {
        ReviewItem.Drift drift => drift.Offer.Drift.Joined is { } joined
            ? $"Added {owner.Catalog.Project(joined)?.Name ?? "the project"} from {owner.Time.Clock(DateTimeOffset.FromUnixTimeMilliseconds(drift.Offer.Drift.At))}"
            : $"Split ended at {owner.Time.Clock(DateTimeOffset.FromUnixTimeMilliseconds(drift.Offer.Drift.At))}",
        ReviewItem.Untracked untracked => $"Added {(untracked.Block.Stretch.Projects.Count > 0 ? owner.Catalog.Project(untracked.Block.Stretch.Projects[0])?.Name : null) ?? "an entry"} {owner.Time.Clock(untracked.Block.Start)}–{owner.Time.Clock(untracked.Block.End)}",
        _ => "Split saved",
    };

    /// <summary>
    /// Once, in the hours after work on a working day, says the day has suggestions waiting in its review, if
    /// notifications for splits are on. Looked for every few minutes then, as the last timer may stop late.
    /// </summary>
    private async Task OfferDayReviewAsync(DateTimeOffset now)
    {
        var preferences = owner.Preferences;
        if (!preferences.NotifyDayReview || !preferences.NotifySwitches || !owner.AgentLogsEnabled || now - dayReviewChecked < TimeSpan.FromMinutes(10)) return;
        var schedule = new ReminderSchedule(preferences.ReminderDays, preferences.ReminderFrom, preferences.ReminderTo, preferences.ReminderEveryMinutes);
        if (schedule.EndedWorkingDay(now, owner.Time) is not { } day || preferences.DayReviewOffered == $"{day:yyyy-MM-dd}") return;
        dayReviewChecked = now;
        await owner.Splits.LoadAsync([day]);
        var items = Review([day]);
        if (items.Count == 0 || owner.Preferences.DayReviewOffered == $"{day:yyyy-MM-dd}") return;
        owner.SavePreferences(owner.Preferences with { DayReviewOffered = $"{day:yyyy-MM-dd}" });
        Toasts.Show(ToastKind.DayReview, $"Today: {items.Count} to review", ReviewItem.Breakdown(items) + ".",
            [("Review", () => owner.OpenReviewAsync(ReviewScope.Day(day)))], () => owner.OpenReviewAsync(ReviewScope.Day(day)));
    }
}
