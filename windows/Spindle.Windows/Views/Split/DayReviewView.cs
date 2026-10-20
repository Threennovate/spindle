using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using Spindle.Activity;
using Spindle.Core;
using Spindle.Windows.Model;

namespace Spindle.Windows.Views;

/// <summary>
/// The day's review: every suggestion the app has for a day (or a week) in its place on the day, to take, change or put
/// off one by one with the keyboard, or all at once. Each row says what the time is now and what it becomes; nothing is
/// saved without a click, and what was dealt with stays in its place with its own Undo until the review closes. As the
/// Mac's DayReviewView, with the web's rows. Change… closes the review with the item, for the window to open the
/// editor; the review opens again after, with the same <see cref="ReviewSession"/>.
/// </summary>
public sealed class DayReviewView : UserControl
{
    /// <summary>What the review closed with: an item to open in the editor, and the days to come back to.</summary>
    public sealed record Changing(ReviewItem Item, ReviewScope Scope);

    private readonly MainWindow owner;
    private readonly ReviewSession session;
    private readonly HashSet<string> busy = [];
    private bool takingAll;
    private bool loaded;
    private List<ReviewItem> items = [];

    public DayReviewView(MainWindow owner, ReviewScope scope, ReviewSession? session = null)
    {
        this.owner = owner;
        this.session = session ?? new ReviewSession();
        Scope = scope;
        Focusable = true;
        Name = "DayReview";
        KeyDown += OnKeyDown;
        owner.Refreshed += Changed;
        DetachedFromVisualTree += (_, _) => owner.Refreshed -= Changed;
        AttachedToVisualTree += async (_, _) =>
        {
            Focus();
            await LoadAsync();
        };
        Render();
    }

    /// <summary>The days shown.</summary>
    public ReviewScope Scope { get; private set; }

    /// <summary>What waits now, in order, for tests.</summary>
    public IReadOnlyList<ReviewItem> Items => items;

    /// <summary>What was dealt with on the days shown, in order.</summary>
    public IReadOnlyList<ReviewDone> Handled => session.Done.Values.Where(d => Scope.Days.Contains(Time.Day(d.Item.Start))).OrderBy(d => d.Item.Start).ToList();

    public ReviewNotice? Notice => session.Notice;

    public string? Selected => session.Selected;

    private UserTime Time => owner.Time;
    private Catalog Catalog => owner.Catalog;

    private void Changed() => Dispatcher.UIThread.Post(Render);

    public async Task LoadAsync()
    {
        var scope = Scope;
        loaded = false;
        Render();
        try
        {
            await owner.Desktop.LoadReviewAsync(scope.Days);
        }
        catch (Exception exception)
        {
            owner.Notify(exception.Message);
        }
        if (scope != Scope) return;
        loaded = true;
        Render();
    }

    private void Render()
    {
        items = owner.Desktop.Review(Scope.Days).Where(i => !session.Done.ContainsKey(i.Id)).ToList();
        Follow();
        var header = Header();
        header.Margin = new Thickness(24, 20, 24, 16);
        var footer = new Border { Child = Footer(), Padding = new Thickness(24, 12) }.Res(Border.BackgroundProperty, "CardBackgroundSecondaryBrush");
        Content = new DockPanel { Children = { header.Dock(Dock.Top), Ui.Divider().Dock(Dock.Top), footer.Dock(Dock.Bottom), Body() } };
    }

    /// <summary>When the selected item goes, e.g. once dealt with, the one now in its place is selected, or the one before at the end.</summary>
    private void Follow()
    {
        var index = items.FindIndex(i => i.Id == session.Selected);
        if (index >= 0)
        {
            session.SelectedIndex = index;
            return;
        }
        session.Selected = items.Count == 0 ? null : items[Math.Min(session.SelectedIndex, items.Count - 1)].Id;
    }

    private void Select(string id)
    {
        session.Selected = id;
        session.SelectedIndex = Math.Max(0, items.FindIndex(i => i.Id == id));
        Render();
        Focus();
    }

    // Header and footer

    private Control Header()
    {
        var mark = new Border { Child = Ui.Icon("IconSparkle", "brand").Centered(), Width = 36, Height = 36, CornerRadius = new CornerRadius(8) }
            .Res(Border.BackgroundProperty, "BrandTintBrush");
        mark.Child!.HorizontalAlignment = HorizontalAlignment.Center;
        var handled = Handled;
        var subtitle = !loaded && items.Count == 0 && handled.Count == 0 ? null
            : items.Count > 0 ? ReviewItem.Breakdown(items)
            : handled.Count > 0 ? "All done"
            : Scope.IsWeek ? "Nothing to review on these days" : "Nothing to review";
        var text = Ui.Stack(2, Ui.Text(Title(), "subtitle").Named("ReviewTitle"), subtitle is null ? null : Ui.Text(subtitle, "secondary").Named("ReviewBreakdown"));
        var today = Time.Today();
        var previous = Ui.IconButton("IconChevronLeft", Scope.IsWeek ? "Previous week" : "Previous day", () => Move(-1));
        var next = Ui.IconButton("IconChevronRight", Scope.IsWeek ? "Next week" : "Next day", () => Move(1));
        next.IsEnabled = !Scope.Days.Contains(today);
        var mode = new Segmented([("Day", "Day"), ("Week", "Week")], Scope.IsWeek ? 1 : 0, index => SwitchTo(week: index == 1));
        var takeable = Takeable();
        var takeAll = Ui.Button(takingAll ? "Saving…" : $"Take all ({takeable.Count})", () => TakeAllAsync(takeable), owner.Notify, "primary").Named("ReviewTakeAll")
            .Tip(takeable.Count < items.Count
                ? "Save every suggestion here as offered; those that need a project or details stay, with one Undo for all"
                : "Save every suggestion here as offered, with one Undo for all");
        takeAll.IsEnabled = takeable.Count > 0 && !takingAll;
        var top = Ui.Columns("Auto,*,Auto,Auto,Auto", 12, mark.Centered(), text.Centered(), Ui.Row(2, previous, next).Centered(), mode, takeAll.Centered());
        var total = items.Count + handled.Count;
        return total == 0 ? top : Ui.Stack(14, top, Progress(handled.Count, total));
    }

    /// <summary>What one click can take now, as "Take all" takes it.</summary>
    private List<ReviewItem> Takeable() => items.Where(i => owner.Desktop.Hold(i) is null && !busy.Contains(i.Id)).ToList();

    /// <summary>"2 of 5 done", as a thin bar under the header.</summary>
    private static Control Progress(int done, int total)
    {
        var bar = new ProgressBar { Minimum = 0, Maximum = total, Value = done, Height = 4, MinHeight = 4, Name = "ReviewProgress" }
            .Spoken($"Reviewed: {done} of {total} done");
        var words = Ui.Text($"{done} of {total} done", "caption", "secondary", "mono").Named("ReviewProgressText");
        return Ui.Columns("*,Auto", 12, bar.Centered(), words.Centered());
    }

    private string Title()
    {
        var today = Time.Today();
        if (!Scope.IsWeek) return $"{Time.DayHeading(Scope.Days[0])} · {Time.LongDay(Scope.Days[0])}";
        if (Scope.Days[^1] == today && Scope.Days[0] == today.AddDays(-6)) return "The last 7 days";
        return CalendarLayout.Label(Scope.Days);
    }

    private Control Footer()
    {
        static Control Hint(string key, string text) => Ui.Row(5, Ui.Keycap(key).Centered(), Ui.Text(text, "caption", "secondary").Centered());
        Control? said = null;
        if (session.Notice is { } notice)
        {
            var line = Ui.Row(8);
            if (!notice.IsError) line.Children.Add(Ui.Icon("IconCheckmark", "brand", "small").Centered());
            var words = Ui.Text(notice.Text, notice.IsError ? "critical" : "").Named("ReviewNotice");
            words.TextTrimming = TextTrimming.CharacterEllipsis;
            line.Children.Add(words.Centered());
            if (notice.Ids.Count > 0)
                line.Children.Add(Ui.Button(Ui.Labeled("IconArrowUndo", "Undo"), () => UndoAsync(notice.Ids), owner.Notify, "subtle").Named("ReviewUndo").Centered());
            said = line;
        }
        else if (items.Count > 0)
        {
            said = Ui.Row(14, Hint("↑↓", "Move"), Hint("Enter", "Take"), Hint("E", "Change"), Hint("Backspace", "Not now"));
        }
        var close = Ui.Button("Close", () => owner.Dialogs.Close(null)).Named("ReviewClose");
        close.MinWidth = 100;
        return Ui.Columns("*,Auto", 12, said?.Centered(), close);
    }

    // Content

    private Control Body()
    {
        var handled = Handled;
        if (items.Count == 0 && handled.Count == 0)
        {
            var title = loaded ? "Nothing to review" : "Reading agent logs…";
            var detail = loaded
                ? Scope.IsWeek ? "No splits or untracked agent time on these days." : "No splits or untracked agent time on this day."
                : "Reading agent logs for these days.";
            var empty = Ui.Stack(6, Ui.Icon(loaded ? "IconCheckmarkCircleFilled" : "IconSparkle", "secondary", "large"), Ui.Text(title, "bodyLarge"), Ui.Wrapped(detail, "secondary"));
            foreach (var child in empty.Children) child.HorizontalAlignment = HorizontalAlignment.Center;
            ((TextBlock)empty.Children[2]).TextAlignment = TextAlignment.Center;
            empty.Margin = new Thickness(48, 64);
            empty.Name = "ReviewEmpty";
            return empty;
        }
        var sections = Ui.Stack(22);
        if (loaded && items.Count == 0) sections.Children.Add(AllDone());
        foreach (var day in items.Select(i => Time.Day(i.Start)).Concat(handled.Select(d => Time.Day(d.Item.Start))).Distinct().Order())
            sections.Children.Add(DaySection(day, items.Where(i => Time.Day(i.Start) == day).ToList(), handled.Where(d => Time.Day(d.Item.Start) == day).ToList()));
        sections.Margin = new Thickness(24, 18, 24, 20);
        var scroller = new ScrollViewer { Content = sections, MaxHeight = 520 };
        // Keeps the selected item in view.
        Dispatcher.UIThread.Post(() =>
        {
            if (sections.GetLogicalDescendants().OfType<Control>().FirstOrDefault(c => c.Name == "ReviewItem" && c.Tag as string == session.Selected) is { } row)
                row.BringIntoView();
        }, DispatcherPriority.Background);
        return scroller;
    }

    /// <summary>Once nothing waits: the review has an end, and the done rows stay, with Undo.</summary>
    private static Control AllDone()
    {
        var words = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        words.Inlines!.Add(new Avalonia.Controls.Documents.Run("All done. ") { FontWeight = FontWeight.SemiBold });
        var rest = new Avalonia.Controls.Documents.Run("Everything here is answered. You can undo until you close the review.");
        rest.Bind(Avalonia.Controls.Documents.TextElement.ForegroundProperty, words.GetResourceObservable("TextSecondaryBrush"));
        words.Inlines.Add(rest);
        return new Border { Child = Ui.Columns("Auto,*", 10, Ui.Icon("IconCheckmarkCircleFilled", "brand").Centered(), words), Padding = new Thickness(12, 10), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1) }
            .Res(Border.BackgroundProperty, "BrandTintBrush").Res(Border.BorderBrushProperty, "BrandBrush").Named("ReviewAllDone");
    }

    private Control DaySection(DateOnly day, List<ReviewItem> dayItems, List<ReviewDone> dayDone)
    {
        var stack = Ui.Stack(8);
        if (Scope.IsWeek)
            stack.Children.Add(Ui.Row(8, Ui.Text(Time.DayHeading(day), "strong"), dayItems.Count > 0 ? Ui.Text($"{dayItems.Count} to review", "secondary").Centered() : null));
        stack.Children.Add(Strip(day, dayItems).Margin(0, 0, 0, 4));
        // A single day shows its other entries too, so each suggestion sits in its place on the day. Entries an item is
        // about, or that a take or the editor made, belong to that item's row.
        var dayStart = Time.StartOf(day);
        var dayEnd = Time.StartOf(day.AddDays(1));
        var now = DateTimeOffset.UtcNow;
        var own = dayItems.Select(i => i.Entry?.Id).Concat(dayDone.Select(d => d.Item.Entry?.Id)).OfType<Guid>()
            .Concat(dayDone.SelectMany(d => d.Changes.SelectMany(c => c.Added))).ToHashSet();
        var others = Scope.IsWeek ? [] : owner.Entries.Where(e => e.Start < dayEnd && (e.Stop ?? now) > dayStart && !own.Contains(e.Id)).ToList();
        var rows = dayItems.Select(i => (Start: i.Start, Control: Row(i)))
            .Concat(dayDone.Select(d => (Start: d.Item.Start, Control: DoneRow(d))))
            .Concat(others.Select(e => (Start: e.Start, Control: ContextRow(e)))).OrderBy(r => r.Start);
        foreach (var (_, control) in rows) stack.Children.Add(control);
        return stack;
    }

    /// <summary>
    /// One suggestion: what the time is now and what it becomes, with its evidence, and its answers. The main button is
    /// what one click can do: take it ("Split", "Split at 10:42", "Add entry"), or, when one click can't, "Change…".
    /// </summary>
    private Control Row(ReviewItem item)
    {
        var isSelected = item.Id == session.Selected;
        var isBusy = busy.Contains(item.Id) || takingAll;
        var hold = owner.Desktop.Hold(item);
        var rail = new Border { Width = 4, CornerRadius = new CornerRadius(2), VerticalAlignment = VerticalAlignment.Stretch };
        if (item.Entry is { } railEntry && Ui.ProjectBrush(Catalog.Project(railEntry.ProjectId)?.Color) is { } color) rail.Background = color;
        else rail.Res(Border.BackgroundProperty, "DividerBrush");
        var text = Ui.Stack(8, Heading(item), ChangeLine(item), Detail(item, hold));
        var times = Ui.Row(10, Ui.Text(Time.Span(item.Start, item.End), "secondary", "mono"), Ui.Text(Durations.Format((item.End - item.Start).TotalSeconds, DurationFormat.Hm), "strong", "mono"));
        times.HorizontalAlignment = HorizontalAlignment.Right;
        var notNow = Ui.Button("Not now", () => PutOff(item), "subtle").Tip("Leave it out of the review; nothing changes (Backspace)").Named("ReviewNotNow");
        var buttons = Ui.Row(6, notNow);
        if (hold is null)
        {
            buttons.Children.Add(Ui.Button("Change…", () => Change(item)).Tip("Open it in the editor with this filled in (E)").Named("ReviewChange"));
            buttons.Children.Add(Ui.Button(owner.Splits.Action(item), () => TakeAsync(item), owner.Notify, "primary").Named("ReviewTake").Tip("Save it as offered, with Undo (Enter)"));
        }
        else
        {
            buttons.Children.Add(Ui.Button("Change…", () => Change(item), "primary").Named("ReviewChange").Tip("One click can't save this; open it in the editor (Enter or E)"));
        }
        foreach (var button in buttons.Children) button.IsEnabled = !isBusy;
        buttons.HorizontalAlignment = HorizontalAlignment.Right;
        var trailing = Ui.Stack(8, times, buttons);
        var grid = Ui.Columns("Auto,*,Auto", 12, rail, text, trailing);
        var row = new Border { Child = grid, Padding = new Thickness(12), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(isSelected ? 1.5 : 1), Tag = item.Id, Opacity = isBusy ? 0.5 : 1 }
            .Res(Border.BorderBrushProperty, isSelected ? "BrandBrush" : "CardStrokeBrush")
            .Res(Border.BackgroundProperty, isSelected ? "BrandTintBrush" : "CardBackgroundBrush")
            .Named("ReviewItem")
            .Spoken($"{(item.Entry is null ? "Untracked time" : "Split")}: {(item.Entry is { } spoken ? spoken.Description.Length == 0 ? "No description" : spoken.Description : "agents were at work")}, {Time.Span(item.Start, item.End)}. Becomes {OutcomeWords(owner.Splits.Outcomes(item))}.");
        row.PointerPressed += (_, _) => Select(item.Id);
        return row;
    }

    private Control Heading(ReviewItem item)
    {
        if (item.Entry is not { } entry)
            return Ui.Row(6, Ui.Text("Not tracked", "medium"), Ui.Text("· agents were at work", "secondary"));
        var description = Ui.Text(entry.Description.Length == 0 ? "No description" : entry.Description, "medium", entry.Description.Length == 0 ? "secondary" : "");
        description.TextTrimming = TextTrimming.CharacterEllipsis;
        Control project = entry.IsSplit() ? SplitViews.Label(Catalog, entry.Parts()) : SplitViews.ProjectOrNone(Catalog, entry.ProjectId, entry.TaskId);
        return Ui.Row(8, description, project);
    }

    /// <summary>"✦ ● Atlas 1:30 → 2 entries: ● Atlas 09:00–09:40 · ● Beacon 09:40–10:30": the time as it is, struck through, and what it becomes.</summary>
    private Control ChangeLine(ReviewItem item)
    {
        var results = owner.Splits.Outcomes(item);
        var weak = item is ReviewItem.Split { Offer.Suggestion.Strength: SplitStrength.Weak };
        var line = new WrapPanel { Orientation = Orientation.Horizontal };
        void Add(Control control) => line.Children.Add(control.Margin(0, 1, 6, 1));
        Add(Ui.Icon("IconSparkle", weak ? "secondary" : "brand", "small").Centered());
        var seconds = (long)(item.End - item.Start).TotalSeconds;
        if (item.Entry is { } entry)
        {
            if (entry.IsSplit())
            {
                var parts = entry.Parts().ToList();
                var shares = SplitMath.Allocate((long)Math.Round(seconds / 60d), parts.Select(p => p.Percent).ToArray());
                for (var index = 0; index < parts.Count; index++)
                {
                    if (index > 0) Add(Ui.Text("·", "tertiary").Centered());
                    Add(Piece(Catalog.Project(parts[index].ProjectId)?.Color, Catalog.Project(parts[index].ProjectId)?.Name ?? "No project", Durations.ShortClock(shares[index] * 60), struck: true));
                }
            }
            else Add(Piece(Catalog.Project(entry.ProjectId)?.Color, Catalog.Project(entry.ProjectId)?.Name ?? "No project", Durations.ShortClock(seconds), struck: true));
        }
        else Add(Ui.Text("Nothing tracked", "secondary").Centered());
        Add(Ui.Text("→", "secondary").Spoken("becomes").Centered());
        if (results.Count > 1) Add(Ui.Text($"{results.Count} entries:", "secondary").Centered());
        else if (item.Entry is null) Add(Ui.Text("New entry", "secondary").Centered());
        for (var index = 0; index < results.Count; index++)
        {
            var result = results[index];
            if (index > 0) Add(Ui.Text("·", "tertiary").Centered());
            for (var part = 0; part < result.Parts.Count; part++)
            {
                var (target, partSeconds) = result.Parts[part];
                if (part > 0) Add(Ui.Text("·", "tertiary").Centered());
                var named = target.ProjectId is not null || target.Repository is not null;
                Add(Piece(named ? SplitChips.ColorOf(owner, target) : null, named ? EvidenceStrip.NameOf(owner, target) : "No project",
                    results.Count > 1 ? $"{Time.Clock(result.Start)}–{Time.Clock(result.End)}" : Durations.ShortClock(partSeconds)));
            }
        }
        return new Border { Child = line, Padding = new Thickness(10, 5), CornerRadius = new CornerRadius(6), HorizontalAlignment = HorizontalAlignment.Left }
            .Res(Border.BackgroundProperty, "SubtleFillHoverBrush").Named("ReviewChangeLine");
    }

    /// <summary>"● Atlas 0:40": a project or repository with its time; struck through for the time as it is now.</summary>
    private static Control Piece(string? color, string name, string time, bool struck = false)
    {
        var dot = Ui.Dot(color, 8);
        if (color is null) dot.Res(Shape.FillProperty, "TextTertiaryBrush");
        if (struck) dot.Opacity = 0.6;
        var label = Ui.Text(name, struck ? "secondary" : "medium");
        var clock = Ui.Text(time, "secondary", "mono");
        if (struck)
        {
            // Their own, not the shared TextDecorations.Strikethrough, which belongs to the thread that first used it.
            label.TextDecorations = [new TextDecoration { Location = TextDecorationLocation.Strikethrough }];
            clock.TextDecorations = [new TextDecoration { Location = TextDecorationLocation.Strikethrough }];
        }
        return Ui.Row(5, dot.Centered(), label.Centered(), clock.Centered());
    }

    /// <summary>The outcome in words, to read out: "2 entries: Atlas 09:00 to 09:40, Beacon 09:40 to 10:30".</summary>
    private string OutcomeWords(IReadOnlyList<ReviewResult> results)
    {
        string Name(SplitTarget target) => target.ProjectId is not null || target.Repository is not null ? EvidenceStrip.NameOf(owner, target) : "no project";
        if (results.Count > 1)
            return $"{results.Count} entries: {string.Join(", ", results.Select(r => $"{Name(r.Parts[0].Target)} {Time.Clock(r.Start)} to {Time.Clock(r.End)}"))}";
        return results.Count == 0 ? "" : string.Join(", ", results[0].Parts.Select(p => $"{Name(p.Target)} {Durations.ShortClock(p.Seconds)}"));
    }

    private Control Detail(ReviewItem item, ReviewHold? hold)
    {
        var stack = Ui.Stack(6);
        switch (item)
        {
            case ReviewItem.Split split:
            {
                var suggestion = split.Offer.Suggestion;
                stack.Children.Add(new EvidenceStrip(owner, suggestion) { MaxWidth = 360, HorizontalAlignment = HorizontalAlignment.Left });
                if (suggestion.Strength == SplitStrength.Weak)
                    stack.Children.Add(Ui.Wrapped($"The logs back this only lightly: {(int)Math.Round(suggestion.Coverage * 100)}% of the time, few prompts.", "caption", "secondary"));
                foreach (var repository in suggestion.Parts.Where(p => p.Target.ProjectId is null && p.Target.Repository is not null).Select(p => p.Target.Repository!))
                {
                    var link = Ui.Button(Ui.Row(4, Ui.Icon("IconLink", "small").Centered(), Ui.Text($"Choose a project for {repository.Name}", "caption")), () => { }, "link")
                        .Tip("It is remembered for your account, on the web too.");
                    link.HorizontalAlignment = HorizontalAlignment.Left;
                    link.Click += (_, _) => PickerList.Show(link, ProjectPicker.Create(owner, null, null, async (project, _) =>
                    {
                        if (project is { } chosen) await owner.Splits.LinkAsync(repository, chosen);
                        else await RepositoryLinkButton.UnlinkAsync(owner, repository);
                    }, choosingAnew: true, placeholder: $"Which project is {repository.Name} for?"));
                    stack.Children.Add(link);
                }
                break;
            }
            case ReviewItem.Drift drift:
                stack.Children.Add(Ui.Wrapped($"{owner.Splits.Headline(drift.Offer.Drift)}.", "caption", "secondary"));
                break;
            case ReviewItem.Untracked { Block.Stretch: { Unlinked: > 0, Projects.Count: > 0 } stretch }:
                stack.Children.Add(Ui.Wrapped($"And {Durations.HoursMinutes(stretch.Unlinked / 1000d)} in repositories without a project.", "caption", "secondary"));
                break;
        }
        if (hold?.Reason is { } reason) stack.Children.Add(Ui.Wrapped(reason, "caption", "secondary").Named("ReviewReason"));
        return stack;
    }

    /// <summary>An item dealt with, in its place: what became of it, with Undo while the review is open.</summary>
    private Control DoneRow(ReviewDone done)
    {
        var item = done.Item;
        var putOff = done.Outcome == ReviewOutcome.NotNow;
        var line = Ui.Row(8);
        if (!putOff) line.Children.Add(Ui.Icon("IconCheckmark", "brand", "small").Centered());
        line.Children.Add(Ui.Text(done.Title, putOff ? "secondary" : "medium").Centered());
        // "Added Atlas 14:00–14:47" says its times already.
        if (!(done.Outcome == ReviewOutcome.Taken && item is ReviewItem.Untracked))
        {
            var description = item.Entry is { } entry ? entry.Description.Length == 0 ? "No description" : entry.Description : "Not tracked";
            var detail = Ui.Text($"{Time.Clock(item.Start)}–{Time.Clock(item.End)} · {description}", "secondary");
            detail.TextTrimming = TextTrimming.CharacterEllipsis;
            line.Children.Add(detail.Centered());
        }
        Control? undo = done.Outcome == ReviewOutcome.Changed ? null
            : Ui.Button(Ui.Labeled("IconArrowUndo", "Undo"), () => UndoAsync([item.Id]), owner.Notify, "subtle").Named("ReviewDoneUndo").Spoken($"Undo: {done.Title}");
        var row = new Border { Child = Ui.Columns("*,Auto", 8, line, undo?.Centered()), Padding = new Thickness(12, 4), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Tag = item.Id }
            .Res(Border.BorderBrushProperty, "CardStrokeBrush").Named("ReviewDone");
        if (!putOff) row.Res(Border.BackgroundProperty, "CardBackgroundBrush");
        return row;
    }

    /// <summary>Another entry of the day, between the suggestions, so they read in place.</summary>
    private Control ContextRow(TimeEntryResponse entry)
    {
        var bar = new Border { Width = 3, Height = 16, CornerRadius = new CornerRadius(1.5), Background = Ui.ProjectBrush(Catalog.Project(entry.ProjectId)?.Color, 0.5) };
        if (bar.Background is null) bar.Res(Border.BackgroundProperty, "DividerBrush");
        var description = Ui.Text(entry.Description.Length == 0 ? "No description" : entry.Description, "secondary");
        Control project = entry.IsSplit() ? SplitViews.Label(Catalog, entry.Parts()) : SplitViews.ProjectOrNone(Catalog, entry.ProjectId, entry.TaskId);
        project.Opacity = 0.8;
        var row = Ui.Columns("Auto,Auto,*,Auto", 12, bar.Centered(), description.Centered(), project.Centered(), Ui.Text(Time.Span(entry.Start, entry.Stop), "secondary", "mono").Centered());
        row.Margin = new Thickness(12, 2);
        return row.Named("ReviewContext");
    }

    /// <summary>
    /// The day at a glance: its entries as bars, the time each suggestion is about outlined in marigold over them, and
    /// untracked time as a dashed outline. Clicking an outline selects its item.
    /// </summary>
    private Control Strip(DateOnly day, List<ReviewItem> dayItems)
    {
        var dayStart = Time.StartOf(day);
        var dayEnd = Time.StartOf(day.AddDays(1));
        var now = DateTimeOffset.UtcNow;
        var entries = owner.Entries.Where(e => e.Start < dayEnd && (e.Stop ?? now) > dayStart).ToArray();
        var spans = entries.Select(e => (Start: e.Start, End: e.Stop ?? now)).Concat(dayItems.Select(i => (i.Start, i.End))).ToArray();
        var first = spans.Length > 0 ? spans.Min(s => s.Start) : dayStart.AddHours(9);
        var last = spans.Length > 0 ? spans.Max(s => s.End) : first.AddHours(8);
        // Whole hours around what is there, at least four of them.
        static DateTimeOffset Hour(DateTimeOffset value, bool up)
        {
            var ticks = value.UtcTicks - value.UtcTicks % TimeSpan.TicksPerHour;
            if (up && ticks < value.UtcTicks) ticks += TimeSpan.TicksPerHour;
            return new DateTimeOffset(ticks, TimeSpan.Zero);
        }
        var from = Hour(first, false) < dayStart ? dayStart : Hour(first, false);
        var to = Hour(last, true) < from.AddHours(4) ? from.AddHours(4) : Hour(last, true);
        var hours = (int)(to - from).TotalHours;
        var step = hours > 12 ? 3 : hours > 6 ? 2 : 1;
        double X(DateTimeOffset at) => Math.Clamp((at - from).TotalSeconds / (to - from).TotalSeconds, 0, 1);
        var strip = new StripPanel { Height = 46 };
        strip.Add(new Border { CornerRadius = new CornerRadius(6) }.Res(Border.BackgroundProperty, "SubtleFillHoverBrush"), 0, 1, 0, 30);
        for (var hour = 0; hour <= hours; hour += step)
        {
            var at = from.AddHours(hour);
            strip.Add(new Border { Width = 1 }.Res(Border.BackgroundProperty, "DividerBrush"), X(at), null, 0, 30);
            var label = Ui.Text(Time.Clock(at), "caption", "tertiary", "mono");
            label.FontSize = 10;
            strip.Add(label, X(at), null, 32, 14, centered: true);
        }
        foreach (var entry in entries)
        {
            var bar = new Border { CornerRadius = new CornerRadius(4), Background = Ui.ProjectBrush(Catalog.Project(entry.ProjectId)?.Color, 0.35) };
            if (bar.Background is null) bar.Res(Border.BackgroundProperty, "ChipBackgroundBrush");
            strip.Add(bar, X(entry.Start), X(entry.Stop ?? now), 6, 18, minimum: 2);
        }
        foreach (var item in dayItems)
        {
            var emphasis = item.Id == session.Selected;
            var outline = new Rectangle { RadiusX = 4, RadiusY = 4, StrokeThickness = emphasis ? 2 : 1.25, Opacity = emphasis ? 1 : 0.85, Cursor = new Cursor(StandardCursorType.Hand), Name = "ReviewOutline", Tag = item.Id }
                .Res(Shape.StrokeProperty, "BrandBrush").Res(Shape.FillProperty, "BrandTintBrush");
            if (item.Entry is null) outline.StrokeDashArray = [3, 2];
            ToolTip.SetTip(outline, $"{Time.Span(item.Start, item.End)}: {owner.Splits.Action(item)}");
            outline.PointerPressed += (_, e) =>
            {
                Select(item.Id);
                e.Handled = true;
            };
            strip.Add(outline, X(item.Start), X(item.End), 2, 26, minimum: 4);
        }
        return strip.Named("ReviewStrip");
    }

    // Keyboard

    /// <summary>
    /// ↑↓ move, Enter takes (or opens the editor when one click can't), E opens the editor, Backspace puts off; Ctrl+←
    /// and Ctrl+→ go to the day or week before or after.
    /// </summary>
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        var index = items.FindIndex(i => i.Id == session.Selected);
        var command = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        switch (e.Key)
        {
            case Key.Left or Key.Right when command:
                Move(e.Key == Key.Left ? -1 : 1);
                break;
            case Key.Up or Key.Down:
                var next = e.Key == Key.Up ? (index < 0 ? items.Count : index) - 1 : index + 1;
                if (next >= 0 && next < items.Count) Select(items[next].Id);
                break;
            case Key.Enter when index >= 0:
                _ = TakeAsync(items[index]);
                break;
            case Key.E when index >= 0 && !command:
                Change(items[index]);
                break;
            case Key.Back or Key.Delete when index >= 0:
                PutOff(items[index]);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    // Actions

    /// <summary>Takes an item with one click, or opens it in the editor when one click can't.</summary>
    public async Task TakeAsync(ReviewItem item)
    {
        if (busy.Contains(item.Id) || takingAll) return;
        if (owner.Desktop.Hold(item) is not null)
        {
            Change(item);
            return;
        }
        busy.Add(item.Id);
        Render();
        try
        {
            if (await owner.Desktop.ApplyAsync(item) is { } change)
            {
                var title = owner.Desktop.Taken(item);
                session.Mark(item, ReviewOutcome.Taken, title, [change]);
                session.Notice = new ReviewNotice(title, [item.Id]);
            }
        }
        catch (Exception exception)
        {
            session.Notice = new ReviewNotice(exception.Message.Length > 0 ? exception.Message : "Couldn't save it.", [], IsError: true);
        }
        finally
        {
            busy.Remove(item.Id);
            Render();
            Focus();
        }
    }

    /// <summary>Takes every item one click can take, one after another, with one Undo for all of them; the rest stay.</summary>
    public async Task TakeAllAsync(IReadOnlyList<ReviewItem> takeable)
    {
        var waiting = items.Count;
        takingAll = true;
        busy.UnionWith(takeable.Select(i => i.Id));
        Render();
        var taken = new List<string>();
        var failed = 0;
        try
        {
            foreach (var item in takeable)
            {
                try
                {
                    if (await owner.Desktop.ApplyAsync(item) is not { } change) continue;
                    session.Mark(item, ReviewOutcome.Taken, owner.Desktop.Taken(item), [change]);
                    taken.Add(item.Id);
                }
                catch (Exception)
                {
                    failed++;
                }
            }
        }
        finally
        {
            busy.ExceptWith(takeable.Select(i => i.Id));
            takingAll = false;
            var left = waiting - taken.Count - failed;
            var text = string.Join(" · ", new[]
            {
                taken.Count == 1 ? "Saved 1 suggestion" : $"Saved {taken.Count} suggestions",
                failed > 0 ? $"{failed} couldn't be saved" : null,
                left > 0 ? $"{left} {(left == 1 ? "needs" : "need")} a project or details" : null,
            }.OfType<string>());
            session.Notice = new ReviewNotice(text, taken, IsError: taken.Count == 0 && failed > 0);
            Render();
            Focus();
        }
    }

    /// <summary>Takes back what was done with these items: takes are undone, newest first, and "Not now" brings the item back.</summary>
    public async Task UndoAsync(IReadOnlyList<string> ids)
    {
        var done = ids.Select(id => session.Done.GetValueOrDefault(id)).OfType<ReviewDone>().OrderBy(d => d.Order).ToList();
        try
        {
            await owner.Desktop.UndoTakesAsync(done.Where(d => d.Outcome == ReviewOutcome.Taken).SelectMany(d => d.Changes).ToList());
            foreach (var putOff in done.Where(d => d.Outcome == ReviewOutcome.NotNow)) owner.Splits.Restore(putOff.Item);
            foreach (var each in done) session.Done.Remove(each.Item.Id);
            session.Notice = null;
            if (done.OrderBy(d => d.Item.Start).FirstOrDefault() is { } first) session.Selected = first.Item.Id;
        }
        catch (Exception exception)
        {
            session.Notice = new ReviewNotice(exception.Message.Length > 0 ? exception.Message : "Couldn't undo.", [], IsError: true);
        }
        Render();
        Focus();
    }

    private void Change(ReviewItem item) => owner.Dialogs.Close(new Changing(item, Scope));

    private void PutOff(ReviewItem item)
    {
        session.Mark(item, ReviewOutcome.NotNow, "Not now");
        owner.Splits.PutOff(item);
        Render();
        Focus();
    }

    private void Move(int steps)
    {
        var day = Scope.Days[0].AddDays(steps * (Scope.IsWeek ? 7 : 1));
        session.Notice = null;
        Show(Scope.IsWeek ? ReviewScope.Week(day, Time) : ReviewScope.Day(day));
    }

    private void SwitchTo(bool week)
    {
        if (week == Scope.IsWeek) return;
        var today = Time.Today();
        // From a week to the day the selection is on, or the last day shown up to today.
        var day = items.FirstOrDefault(i => i.Id == session.Selected) is { } item ? Time.Day(item.Start) : Scope.Days.Where(d => d <= today).DefaultIfEmpty(Scope.Days[0]).Max();
        Show(week ? ReviewScope.Week(day, Time) : ReviewScope.Day(day));
    }

    private void Show(ReviewScope scope)
    {
        Scope = scope;
        session.Selected = null;
        session.SelectedIndex = 0;
        _ = LoadAsync();
    }
}

/// <summary>"✦ 3 to review": opens a day's review, beside the day in the entry list and the calendar.</summary>
internal static class ReviewViews
{
    public static Button DayButton(MainWindow owner, int count, ReviewScope scope)
    {
        var label = Ui.Row(4, Ui.Icon("IconSparkle", "brand", "small").Centered(), Ui.Text($"{count} to review", "caption", "medium", "mono").Centered());
        var button = Ui.Button(label, () => owner.OpenReviewAsync(scope), owner.Notify, "chip", "review").Named("ReviewDay")
            .Tip("Review this day's suggestions in one place").Spoken($"{count} to review");
        button.VerticalAlignment = VerticalAlignment.Center;
        button.HorizontalAlignment = HorizontalAlignment.Center;
        return button;
    }

    /// <summary>How far back the card above the timer looks for the last day with something to review, e.g. Friday on a Monday.</summary>
    public const int LookBack = 3;

    /// <summary>
    /// "Yesterday: 3 to review", above the timer the next morning: the last day's suggestions, waiting quietly rather
    /// than in a dialog. Gone once dealt with, or put off for that day.
    /// </summary>
    public static Control? Card(MainWindow owner, DateOnly? on = null)
    {
        if (!owner.AgentLogsEnabled) return null;
        var time = owner.Time;
        var today = on ?? time.Today();
        foreach (var day in Enumerable.Range(1, LookBack).Select(i => today.AddDays(-i)))
        {
            var items = owner.Desktop.Review([day]);
            if (items.Count == 0) continue;
            if (owner.Preferences.ReviewCardDismissed == $"{day:yyyy-MM-dd}") return null;
            var notNow = Ui.Button("Not now", () =>
            {
                owner.SavePreferences(owner.Preferences with { ReviewCardDismissed = $"{day:yyyy-MM-dd}" });
                owner.RefreshViews();
            }, "subtle")
                .Tip("Hide this until the next day; the review stays in the day's header");
            var review = Ui.Button("Review…", () => owner.OpenReviewAsync(ReviewScope.Day(day)), owner.Notify, "primary").Named("ReviewCardOpen");
            var text = Ui.Stack(1, Ui.Text($"{time.DayHeading(day)}: {items.Count} to review", "strong"), Ui.Text(ReviewItem.Breakdown(items), "secondary"));
            var row = Ui.Columns("Auto,*,Auto", 14, Ui.Icon("IconSparkle", "brand", "large").Centered(), text.Centered(), Ui.Row(8, notNow, review).Centered());
            var card = Ui.Card(row);
            card.Margin = new Thickness(0, 12, 0, 0);
            return card.Named("ReviewCard");
        }
        return null;
    }
}
