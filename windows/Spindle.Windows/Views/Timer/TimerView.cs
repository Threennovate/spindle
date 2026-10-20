using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.VisualTree;
using Spindle.Activity;
using Spindle.Core;
using Spindle.Windows.Model;

namespace Spindle.Windows.Views;

/// <summary>The timer bar above the person's entries, a week at a time, grouped by day, as on the Mac.</summary>
public sealed class TimerView : UserControl, ILivePage
{
    private readonly MainWindow owner;
    private readonly TimerBar bar;
    private readonly StackPanel list = new() { Spacing = 0 };
    private readonly ContentControl reviewCard = new();
    private readonly List<(TextBlock Text, TimeEntryResponse Entry)> runningDurations = [];
    private readonly List<(TextBlock Text, DateOnly Day)> runningTotals = [];
    private DateOnly loadedSince;

    public TimerView(MainWindow owner)
    {
        this.owner = owner;
        bar = new TimerBar(owner);
        loadedSince = owner.Time.WeekStartDay(owner.Time.Today()).AddDays(-21);
        var page = Ui.Stack(0, Ui.Text("Timer", "title").Margin(0, 0, 0, 16), bar, AgentLogsOffer(), reviewCard, list.Margin(0, 16, 0, 0));
        page.Margin = new Thickness(32, 20, 32, 32);
        Content = new ScrollViewer { Content = page };
        Render();
    }

    internal TimerBar Bar => bar;
    public void FocusDescription() => bar.FocusDescription();
    public Task StartAsync() => bar.StartAsync();

    public void Refresh()
    {
        bar.Refresh();
        Render();
    }

    public void Tick()
    {
        bar.Tick();
        var now = DateTimeOffset.UtcNow;
        foreach (var (text, entry) in runningDurations)
        {
            if (owner.Running?.Id == entry.Id) text.Text = Durations.Clock(entry.Seconds(now));
        }
        foreach (var (text, day) in runningTotals) text.Text = owner.Time.Duration(DaySeconds(day, now));
    }

    /// <summary>Asks once whether to suggest splits from agent logs, when this PC has logs of an agent Spindle reads.</summary>
    private Control? AgentLogsOffer()
    {
        if (owner.Preferences.AgentLogsDecided || owner.Preferences.AgentLogsEnabled || !owner.Activity.HasLogs) return null;
        var turnOn = Ui.Button("Turn on", () =>
        {
            owner.SavePreferences(owner.Preferences with { AgentLogsEnabled = true, AgentLogsDecided = true });
            // Shows what the last week's logs found, so the point shows now rather than when an entry stops.
            _ = owner.ShowLogsFoundAsync();
            return Task.CompletedTask;
        }, owner.Notify, "primary");
        var notNow = Ui.Button("Not now", () =>
        {
            owner.SavePreferences(owner.Preferences with { AgentLogsDecided = true });
            return Task.CompletedTask;
        }, owner.Notify);
        var text = Ui.Stack(4,
            Ui.Text("Split parallel work between projects", "bodyLarge"),
            Ui.Wrapped($"Spindle can read {AgentLogFormat.Names} logs on this PC to notice when an entry's time went to more than one project, and offer to split it. Only times, folders and session titles are kept, and the logs stay on this PC.", "secondary"),
            Ui.Row(8, turnOn, notNow).Margin(0, 6, 0, 0));
        var card = Ui.Card(Ui.Columns("Auto,*", 14, Ui.Icon("IconSparkle", "brand", "large"), text));
        card.Margin = new Thickness(0, 12, 0, 0);
        card.Name = "AgentLogsOffer";
        return card;
    }

    private IEnumerable<TimeEntryResponse> Shown() => owner.Entries.Where(e => owner.Time.Day(e.Start) >= loadedSince);

    private double DaySeconds(DateOnly day, DateTimeOffset now) => Shown().Where(e => owner.Time.Day(e.Start) == day).Sum(e => e.Seconds(now));

    private void Render()
    {
        // Agent activity of the days shown, to offer splitting entries whose time went to several projects.
        // The last few days too, for the card about the last day with something to review.
        var today = owner.Time.Today();
        if (owner.AgentLogsEnabled)
            _ = owner.Splits.LoadAsync(owner.Splits.DaysOf(Shown()).Concat(Enumerable.Range(0, ReviewViews.LookBack + 1).Select(i => today.AddDays(-i))));
        reviewCard.Content = ReviewViews.Card(owner);
        // Worked out once for all the days shown, not once for each day's header.
        var toReview = owner.AgentLogsEnabled
            ? owner.Desktop.Review(Shown().Select(e => owner.Time.Day(e.Start)).Distinct()).GroupBy(i => owner.Time.Day(i.Start)).ToDictionary(g => g.Key, g => g.Count())
            : [];
        runningDurations.Clear();
        runningTotals.Clear();
        list.Children.Clear();
        var now = DateTimeOffset.UtcNow;
        var time = owner.Time;
        var week = time.Week(time.Today());
        var weekSeconds = owner.Entries.Where(e => week.Contains(time.Day(e.Start))).Sum(e => e.Seconds(now));
        var total = Ui.Text(time.Duration(weekSeconds), "strong", "mono");
        list.Children.Add(Ui.Columns("*,Auto,Auto", 6, Ui.Text("This week", "bodyLarge").Centered(), Ui.Text("Total", "secondary").Centered(), total.Centered()).Margin(4, 0, 4, 12));
        var days = Shown().GroupBy(e => time.Day(e.Start)).OrderByDescending(g => g.Key).ToArray();
        if (!days.Any(d => week.Contains(d.Key)))
        {
            var heading = Ui.Text("Nothing tracked yet this week", "strong");
            var hint = Ui.Wrapped("Type what you are working on and press Enter to start. To add time you forgot to track, switch to manual mode.", "secondary");
            heading.TextAlignment = Avalonia.Media.TextAlignment.Center;
            hint.TextAlignment = Avalonia.Media.TextAlignment.Center;
            var empty = Ui.Stack(4, heading, hint);
            empty.Margin = new Thickness(0, 12);
            list.Children.Add(Ui.Card(empty).Margin(0, 0, 0, 16));
        }
        foreach (var day in days)
        {
            var dayTotal = Ui.Text(time.Duration(day.Sum(e => e.Seconds(now))), "strong", "mono");
            if (day.Any(e => e.IsRunning())) runningTotals.Add((dayTotal, day.Key));
            var heading = Ui.Columns("Auto,Auto,*,Auto", 8, Ui.Text(time.DayHeading(day.Key), "strong").Centered(),
                toReview.TryGetValue(day.Key, out var waiting) ? ReviewViews.DayButton(owner, waiting, ReviewScope.Day(day.Key)) : null, null, dayTotal.Centered());
            heading.Margin = new Thickness(4, 8, 4, 8);
            list.Children.Add(heading);
            var rows = Ui.Stack(0);
            var entries = day.OrderByDescending(e => e.Start).ToArray();
            for (var index = 0; index < entries.Length; index++)
            {
                if (index > 0) rows.Children.Add(Ui.Divider().Margin(16, 0, 0, 0));
                rows.Children.Add(Row(entries[index]));
            }
            var card = Ui.Card(rows, "flush");
            card.ClipToBounds = true;
            list.Children.Add(card.Margin(0, 0, 0, 12));
        }
        var more = Ui.Button("Load the week before", LoadWeekBeforeAsync, owner.Notify);
        var since = Ui.Text($"Showing entries since {time.LongDay(loadedSince)}", "caption", "secondary");
        var footer = Ui.Stack(6, more, since);
        footer.HorizontalAlignment = HorizontalAlignment.Center;
        more.HorizontalAlignment = HorizontalAlignment.Center;
        list.Children.Add(footer.Margin(0, 8, 0, 0));
    }

    private async Task LoadWeekBeforeAsync()
    {
        var from = loadedSince.AddDays(-7);
        await owner.LoadEntriesAsync(owner.Time.StartOf(from), owner.Time.StartOf(loadedSince));
        loadedSince = from;
        Render();
    }

    /// <summary>One entry: what it was, when, and for how long; actions under the pointer and in its context menu.</summary>
    private Control Row(TimeEntryResponse entry)
    {
        var catalog = owner.Catalog;
        var time = owner.Time;
        var locked = owner.Rules.IsLocked(time.Day(entry.Start));
        var now = DateTimeOffset.UtcNow;
        var description = Ui.Text(entry.Description.Length == 0 ? "No description" : entry.Description, entry.Description.Length == 0 ? "secondary" : "");
        var details = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        if (entry.IsSplit())
        {
            details.Children.Add(SplitViews.Label(catalog, entry.Parts()));
            var meter = SplitViews.Meter(catalog, entry.Parts());
            meter.Width = 56;
            details.Children.Add(meter);
        }
        else if (catalog.Project(entry.ProjectId) is { } project)
        {
            details.Children.Add(SplitViews.ProjectLabel(project, catalog.Task(entry.TaskId), catalog.Client(project.ClientId)));
        }
        foreach (var tag in entry.TagIds.Select(catalog.Tag).Where(t => t is not null)) details.Children.Add(Ui.Chip(tag!.Name));
        var text = Ui.Stack(3, description, details.Children.Count > 0 ? details : null);
        var trailing = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, VerticalAlignment = VerticalAlignment.Center };
        if (locked) trailing.Children.Add(Ui.Icon("IconLockClosed", "secondary", "small").Centered().Tip("Locked"));
        if (ProposalViews.Chip(owner, entry) is { } proposed) trailing.Children.Add(proposed);
        else if (!locked && SplitChips.ForEntry(owner, entry) is { } chip) trailing.Children.Add(chip);
        if (entry.AnyBillable()) trailing.Children.Add(Ui.Icon("IconMoney", "secondary", "small").Centered().Tip("Billable"));
        trailing.Children.Add(Ui.Text(time.Span(entry.Start, entry.Stop), "secondary", "mono").Centered());
        var duration = entry.IsRunning()
            ? Ui.Text(Durations.Clock(entry.Seconds(now)), "strong", "mono", "brand")
            : Ui.Text(time.Duration(entry.Seconds(now)), "medium", "mono");
        duration.Width = 76;
        duration.TextAlignment = Avalonia.Media.TextAlignment.Right;
        if (entry.IsRunning()) runningDurations.Add((duration, entry));
        trailing.Children.Add(duration.Centered());
        var resume = Ui.IconButton("IconPlayFilled", "Continue", () => owner.ContinueAsync(entry), owner.Notify, "reveal", "small");
        resume.Name = "Continue";
        resume.Classes.Add("brandIcon");
        resume.IsEnabled = !entry.IsRunning();
        if (entry.IsRunning()) resume.Opacity = 0;
        var menu = Menu(entry, locked);
        var more = new Button { Content = Ui.Icon("IconMoreHorizontal"), Flyout = menu }.Classed("icon", "small", "reveal").Tip("More").Spoken("More options").Named("EntryMore");
        trailing.Children.Add(Ui.Row(0, resume, more));
        var row = new Border { Child = Ui.Columns("*,Auto", 12, text.Centered(), trailing) }.Classed("listRow");
        row.ContextFlyout = Menu(entry, locked);
        row.Tag = entry.Id;
        row.PointerReleased += async (_, e) =>
        {
            if (e.InitialPressMouseButton != MouseButton.Left || e.Source is Visual source && IsInsideButton(source)) return;
            await owner.EditAsync(entry);
        };
        return row;
    }

    private static bool IsInsideButton(Visual source)
    {
        for (Visual? visual = source; visual is not null; visual = visual.GetVisualParent())
        {
            if (visual is Button) return true;
            if (visual is Border { Classes: var classes } && classes.Contains("listRow")) return false;
        }
        return false;
    }

    private MenuFlyout Menu(TimeEntryResponse entry, bool locked)
    {
        var menu = new MenuFlyout();
        void Add(string header, string icon, Func<Task> action, bool enabled = true)
        {
            var item = new MenuItem { Header = header, Icon = Ui.Icon(icon), IsEnabled = enabled };
            item.Click += async (_, _) =>
            {
                try
                {
                    await action();
                }
                catch (Exception exception)
                {
                    owner.Notify(exception.Message);
                }
            };
            menu.Items.Add(item);
        }
        Add("Continue", "IconPlay", () => owner.ContinueAsync(entry), !entry.IsRunning());
        Add(locked ? "View…" : "Edit…", locked ? "IconEye" : "IconEdit", () => owner.EditAsync(entry));
        if (!locked)
        {
            if (owner.Splits.Suggestion(entry) is not null)
                Add("Split as agents worked…", "IconSparkle", () => owner.Splits.ReviewAsync(entry));
            Add(entry.IsSplit() ? "Change split…" : "Split time…", "IconArrowSplit", () => owner.EditAsync(entry, start: new EditorStart(StartSplit: !entry.IsSplit())));
        }
        Add("Duplicate", "IconCopy", () => DuplicateAsync(entry), !entry.IsRunning() && !locked);
        menu.Items.Add(new Separator());
        Add("Delete…", "IconDelete", () => DeleteAsync(entry), !locked);
        return menu;
    }

    private async Task DuplicateAsync(TimeEntryResponse entry)
    {
        await owner.MutateAsync(() => owner.CreateEntryAsync(entry.Continuing(entry.Start) with { Stop = entry.Stop }));
        owner.Notify("Time entry duplicated", Shell.NoticeKind.Success);
    }

    public async Task DeleteAsync(TimeEntryResponse entry)
    {
        var duration = owner.Time.Duration(entry.Seconds(DateTimeOffset.UtcNow));
        var message = entry.Description.Length == 0 ? $"The entry and its {duration} will be removed." : $"“{entry.Description}” and its {duration} will be removed.";
        if (!await owner.ConfirmAsync("Delete this time entry?", message, "Delete", destructive: true)) return;
        await owner.MutateAsync(() => owner.DeleteEntryAsync(entry));
        owner.Notify("Time entry deleted", Shell.NoticeKind.Success);
    }
}
