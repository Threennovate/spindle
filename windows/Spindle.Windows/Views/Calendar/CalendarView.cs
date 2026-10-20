using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Spindle.Activity;
using Spindle.Core;
using Spindle.Windows.Desktop;
using Spindle.Windows.Model;
using Spindle.Windows.Views.Shell;

namespace Spindle.Windows.Views;

/// <summary>
/// The entries of a day or a week on a timeline, in project colours, with the current time and, when asked, agent
/// activity beside them. Dragging on an empty stretch adds time; dragging an entry moves it, its edges resize it.
/// </summary>
public sealed class CalendarView : UserControl, ILoadable, ILivePage
{
    public const double HourHeight = 48;
    public const double Gutter = 56;
    private const double FocusWidth = 48;
    private const double LaneWidth = 46;
    private const double WeekStrip = 5;

    private readonly MainWindow owner;
    private readonly TextBlock label = Ui.Text("", "subtitle").Named("CalendarLabel");
    private readonly ContentControl headings = new();
    private readonly CalendarCanvas canvas;
    private readonly ScrollViewer scroller;
    private readonly Segmented mode;
    private readonly Button today;
    private readonly TextBlock status = Ui.Text("", "caption", "secondary");
    private readonly ContentControl review = new();
    private readonly ToggleButton? activityToggle;
    private DateOnly anchor;
    private DateOnly[] scrolledFor = [];

    public CalendarView(MainWindow owner)
    {
        this.owner = owner;
        anchor = owner.Time.Today();
        canvas = new CalendarCanvas(this);
        scroller = new ScrollViewer { Content = canvas, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        var previous = Ui.IconButton("IconChevronLeft", "Previous", () => Move(-1));
        today = Ui.Button("Today", () =>
        {
            anchor = owner.Time.Today();
            _ = LoadSafelyAsync();
        });
        var next = Ui.IconButton("IconChevronRight", "Next", () => Move(1));
        mode = new Segmented([("Day", "Day"), ("Week", "Week")], owner.Preferences.CalendarDayMode ? 0 : 1, index =>
        {
            owner.SavePreferences(owner.Preferences with { CalendarDayMode = index == 0 });
            _ = LoadSafelyAsync();
        });
        var add = Ui.Button(Ui.Labeled("IconAdd", "Add entry"), AddEntryAsync, owner.Notify, "primary").Spoken("Add entry").Named("CalendarAdd");
        if (owner.AgentLogsEnabled)
        {
            // An icon that is filled and tinted while the lanes show, so it says at a glance whether they do.
            activityToggle = new ToggleButton { IsChecked = owner.Preferences.ShowAgentActivity }.Classed("activity").Named("ActivityToggle").Spoken("Agent activity");
            void Show(bool on)
            {
                activityToggle.Content = on ? Ui.Icon("IconSparkleFilled", "brand") : Ui.Icon("IconSparkle", "secondary");
                activityToggle.Tip(on
                    ? "Hide agent activity (Ctrl+Shift+A)"
                    : $"Show agent activity (Ctrl+Shift+A): your focus and agent runs from {AgentLogFormat.Names} beside your entries. Beta");
            }
            Show(activityToggle.IsChecked == true);
            activityToggle.IsCheckedChanged += async (_, _) =>
            {
                Show(activityToggle.IsChecked == true);
                owner.SavePreferences(owner.Preferences with { ShowAgentActivity = activityToggle.IsChecked == true });
                await LoadSafelyAsync();
            };
        }
        var navigation = Ui.Row(4, previous, today, next);
        var header = Ui.Columns("Auto,Auto,*,Auto,Auto,Auto,Auto,Auto", 12, navigation, label.Centered(), null, status.Centered(), review.Centered(), activityToggle, mode, add);
        header.Margin = new Thickness(32, 20, 32, 12);
        var grid = new DockPanel { Children = { headings.Dock(Dock.Top), new Border().Classed("divider").Dock(Dock.Top), scroller } };
        grid.Margin = new Thickness(16, 0, 16, 0);
        Content = new DockPanel { Children = { Ui.Stack(0, Ui.Text("Calendar", "title").Margin(32, 20, 32, 0), header).Dock(Dock.Top), grid } };
        KeyDown += (_, e) =>
        {
            if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
            if (e.Key == Key.A && e.KeyModifiers.HasFlag(KeyModifiers.Shift) && activityToggle is not null)
            {
                activityToggle.IsChecked = activityToggle.IsChecked != true;
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Left) Move(-1);
            else if (e.Key == Key.Right) Move(1);
            else if (e.Key == Key.T) anchor = owner.Time.Today();
            else return;
            _ = LoadSafelyAsync();
            e.Handled = true;
        };
        Focusable = true;
    }

    internal MainWindow Owner => owner;
    internal bool DayMode => mode.Selected == 0;
    internal DateOnly[] Days => DayMode ? [anchor] : owner.Time.Week(anchor);
    internal bool ShowsActivity => owner.AgentLogsEnabled && owner.Preferences.ShowAgentActivity;
    internal IReadOnlyList<DaySegment> Segments { get; private set; } = [];
    /// <summary>What the review offers on the days shown.</summary>
    internal IReadOnlyList<ReviewItem> Reviewing { get; private set; } = [];
    /// <summary>Past agent attention nobody tracked on the days shown.</summary>
    internal IReadOnlyList<UntrackedBlock> Ghosts { get; private set; } = [];

    /// <summary>Shows a day or week, e.g. for snapshots of a given day.</summary>
    internal async Task ShowAsync(DateOnly day, bool dayMode)
    {
        anchor = day;
        mode.Select(dayMode ? 0 : 1);
        await LoadAsync();
    }

    private void Move(int steps)
    {
        anchor = anchor.AddDays(steps * (DayMode ? 1 : 7));
        _ = LoadSafelyAsync();
    }

    private async Task LoadSafelyAsync()
    {
        try
        {
            await LoadAsync();
        }
        catch (Exception exception)
        {
            owner.Notify(exception.Message);
        }
    }

    public async Task LoadAsync()
    {
        var days = Days;
        Render();
        await owner.LoadEntriesAsync(owner.Time.StartOf(days[0]).AddDays(-1), owner.Time.StartOf(days[^1].AddDays(1)));
        if (!days.SequenceEqual(Days)) return;
        if (owner.AgentLogsEnabled)
        {
            status.Text = ShowsActivity ? "Reading agent logs…" : "";
            await owner.Splits.LoadAsync(days);
            status.Text = "";
        }
        Render();
    }

    public void Refresh() => Render();

    public void Tick()
    {
        if (DateTimeOffset.UtcNow.Second == 0 || owner.Running is not null && DateTimeOffset.UtcNow.Second % 15 == 0) Render();
    }

    private void Render()
    {
        var days = Days;
        label.Text = CalendarLayout.Label(days);
        today.IsEnabled = !days.Contains(owner.Time.Today());
        var now = DateTimeOffset.UtcNow;
        Segments = CalendarLayout.Layout(owner.Entries, days, owner.Time, now);
        // Worked out once for the days shown, not once for each day.
        Reviewing = owner.AgentLogsEnabled ? owner.Desktop.Review(days) : [];
        Ghosts = owner.AgentLogsEnabled ? owner.Desktop.Untracked(days) : [];
        review.Content = Reviewing.Count == 0 ? null
            : Ui.Button(Ui.Labeled("IconSparkle", DayMode ? $"Review day ({Reviewing.Count})" : $"Review week ({Reviewing.Count})"),
                () => owner.OpenReviewAsync(DayMode ? ReviewScope.Day(days[0]) : new ReviewScope(days)), owner.Notify).Named("CalendarReview")
                .Tip("Every suggestion for the days shown, in one place");
        headings.Content = Headings(days);
        canvas.InvalidateMeasure();
        canvas.Rebuild();
        if (!scrolledFor.SequenceEqual(days))
        {
            scrolledFor = days;
            var earliest = Segments.Where(s => !s.ContinuesBefore).Select(s => s.StartMinute).DefaultIfEmpty(7 * 60).Min();
            var hour = Math.Max(0, Math.Min(7, (int)(earliest / 60)) - 0.5);
            Dispatcher.UIThread.Post(() => scroller.Offset = new Vector(0, hour * HourHeight), DispatcherPriority.Background);
        }
    }

    internal double ColumnWidth => DayMode && ShowsActivity ? FocusWidth + Lanes.Count * LaneWidth : 0;
    /// <summary>A lane per agent whose logs are read.</summary>
    internal IReadOnlyList<AgentLogFormat> Lanes => AgentLogFormat.All.Where(f => owner.AgentSources.Contains(f.Source)).ToArray();

    private Control Headings(DateOnly[] days)
    {
        var columns = "56," + string.Join(",", days.Select(_ => "*")) + (ColumnWidth > 0 ? $",{ColumnWidth}" : "");
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(columns) };
        var time = owner.Time;
        for (var index = 0; index < days.Length; index++)
        {
            var day = days[index];
            var isToday = day == time.Today();
            var seconds = Segments.Where(s => s.Day == day).Sum(s => (s.EndMinute - s.StartMinute) * 60);
            var weekday = Ui.Text(UserTime.Weekday(day).ToUpperInvariant(), "caption", "medium", "secondary");
            var number = Ui.Text(day.Day.ToString(), "bodyLarge", "mono", isToday ? "onBrand" : "");
            number.HorizontalAlignment = HorizontalAlignment.Center;
            number.VerticalAlignment = VerticalAlignment.Center;
            var circle = new Border { Width = 32, Height = 32, CornerRadius = new CornerRadius(16), Child = number };
            if (isToday) circle.Res(Border.BackgroundProperty, "BrandBrush");
            var total = Ui.Text(seconds > 0 ? Durations.HoursMinutes(seconds) : "–", "caption", "secondary", "mono");
            var top = Ui.Row(4, weekday);
            if (owner.Rules.IsLocked(day)) top.Children.Add(Ui.Icon("IconLockClosed", "secondary", "small").Tip("Locked"));
            var stack = Ui.Stack(2, top, circle, total);
            if (Reviewing.Count(i => time.Day(i.Start) == day) is > 0 and var waiting) stack.Children.Add(ReviewViews.DayButton(owner, waiting, ReviewScope.Day(day)).Margin(0, 2, 0, 0));
            if (ShowsActivity && owner.Splits.Day(day) is { Attention.Count: > 0 } activity)
            {
                var focus = activity.Attention.Sum(a => a.Duration) / 1000d;
                stack.Children.Add(Ui.Row(4, Ui.Icon("IconSparkle", "secondary", "small").Centered(), Ui.Text(Durations.HoursMinutes(focus), "caption", "secondary", "mono")).Tip("Focus in agent logs"));
            }
            foreach (var child in stack.Children) child.HorizontalAlignment = HorizontalAlignment.Center;
            stack.Margin = new Thickness(0, 6, 0, 8);
            grid.Children.Add(stack.Column(index + 1));
        }
        if (ColumnWidth > 0)
        {
            // A lane per source, headed by its short name; the full name is its tip.
            var lanes = Ui.Columns(string.Join(",", [FocusWidth, .. Lanes.Select(_ => LaneWidth)]), 0,
                [Ui.Text("Focus", "caption", "secondary"), .. Lanes.Select(f => f.ShortLabel == f.Label
                    ? Ui.Text(f.Label, "caption", "secondary") : Ui.Text(f.ShortLabel, "caption", "secondary").Tip(f.Label))]);
            foreach (var child in lanes.Children) child.HorizontalAlignment = HorizontalAlignment.Center;
            lanes.VerticalAlignment = VerticalAlignment.Bottom;
            lanes.Margin = new Thickness(0, 0, 0, 10);
            grid.Children.Add(lanes.Column(days.Length + 1));
        }
        return grid;
    }

    private async Task AddEntryAsync()
    {
        var days = Days;
        var time = owner.Time;
        var shown = days.Contains(time.Today()) ? time.Today() : days[0];
        var day = owner.Rules.IsLocked(shown) ? time.Today() : shown;
        var start = CalendarLayout.Snap(time.MinuteOfDay(DateTimeOffset.UtcNow) - 7.5);
        await owner.EditAsync(null, CalendarLayout.Instant(day, start, time), CalendarLayout.Instant(day, Math.Min(start + 60, CalendarLayout.MinutesPerDay), time));
        Render();
    }

    internal async Task FinishAsync(CalendarDrag drag)
    {
        var time = owner.Time;
        switch (drag)
        {
            case CalendarDrag.Open open:
                await owner.EditAsync(open.Entry);
                break;
            case CalendarDrag.Move { Moved: false } move:
                await owner.EditAsync(move.Entry);
                break;
            case CalendarDrag.Resize { Moved: false } resize:
                await owner.EditAsync(resize.Entry);
                break;
            case CalendarDrag.Create create:
                var (from, to) = Math.Abs(create.Current - create.Anchor) < CalendarLayout.Step
                    ? (create.Anchor, Math.Min(create.Anchor + 60, CalendarLayout.MinutesPerDay))
                    : (Math.Min(create.Anchor, create.Current), Math.Max(create.Anchor, create.Current));
                await owner.EditAsync(null, CalendarLayout.Instant(create.Day, from, time), CalendarLayout.Instant(create.Day, to, time));
                break;
            case CalendarDrag.Move move:
                var start = CalendarLayout.Instant(move.Day, move.Start, time);
                await Save(move.Entry, start, start.AddMinutes(move.Duration));
                break;
            case CalendarDrag.Resize { StartEdge: true } resize:
                await Save(resize.Entry, CalendarLayout.Instant(resize.Day, resize.Start, time), resize.Entry.Stop!.Value);
                break;
            case CalendarDrag.Resize resize:
                await Save(resize.Entry, resize.Entry.Start, CalendarLayout.Instant(resize.Day, resize.End, time));
                break;
        }
        Render();
    }

    private async Task Save(TimeEntryResponse entry, DateTimeOffset start, DateTimeOffset stop)
    {
        try
        {
            await owner.MutateAsync(() => owner.UpdateEntryAsync(entry, entry.Request() with { Start = start, Stop = stop }));
        }
        catch (Exception exception)
        {
            owner.Notify(exception.Message);
        }
    }

    /// <summary>The timeline itself: hour lines, entries, the drag preview, the current time and agent activity.</summary>
    private sealed class CalendarCanvas : Canvas
    {
        private readonly CalendarView view;
        private readonly CalendarBackground background;
        private CalendarDrag? drag;
        private Point pressedAt;
        /// <summary>The block whose suggestion is drawn on it, and whether its ✦ has the keyboard focus, kept across rebuilds.</summary>
        private (Guid Entry, DateOnly Day, bool Focused)? previewing;
        private TopLevel? window;
        /// <summary>Where the pointer was when a ✦ was clicked: until it moves, a ✦ that comes up under it draws nothing.</summary>
        private Point? clickedAt;
        private Point pointer;

        public CalendarCanvas(CalendarView view)
        {
            this.view = view;
            Height = 24 * HourHeight;
            Background = Brushes.Transparent;
            ClipToBounds = true;
            background = new CalendarBackground(view);
            SizeChanged += (_, _) => Rebuild();
        }

        private const double ScrollBarRoom = 16;
        private double Width0 => Bounds.Width;
        private double DayWidth => Math.Max(10, (Width0 - Gutter - view.ColumnWidth) / view.Days.Length);
        private double WeekReserve => !view.DayMode && view.ShowsActivity ? WeekStrip + 4 : 0;

        public void Rebuild()
        {
            Children.Clear();
            background.Width = Width0;
            background.Height = Height;
            background.InvalidateVisual();
            Children.Add(background);
            var shown = drag?.Preview() is { } preview ? view.Segments.Where(s => s.Entry.Id != preview.Entry.Id) : view.Segments;
            foreach (var segment in shown) Children.Add(Block(segment, dragging: false));
            if (drag?.Preview() is { } moving) Children.Add(Block(moving, dragging: true));
            if (drag is CalendarDrag.Create create) Children.Add(CreatePreview(create));
            Untracked();
            Proposals();
            Activity();
            NowLine();
            if (previewing is { Focused: true } kept && Children.OfType<Border>().FirstOrDefault(b => b.Tag is DaySegment s && s.Entry.Id == kept.Entry && s.Day == kept.Day) is { } focused
                && focused.GetLogicalDescendants().OfType<Button>().FirstOrDefault(b => b.Name == "CalendarSuggestion") is { } spark)
                spark.Focus(NavigationMethod.Tab);
        }

        private Rect Frame(DaySegment segment)
        {
            var index = Array.IndexOf(view.Days, segment.Day);
            if (index < 0) return default;
            var laneWidth = (DayWidth - WeekReserve) / segment.Lanes;
            return new Rect(
                Gutter + index * DayWidth + segment.Lane * laneWidth + 2,
                segment.StartMinute * HourHeight / 60,
                Math.Max(laneWidth - 4, 4),
                Math.Max((segment.EndMinute - segment.StartMinute) * HourHeight / 60, 14));
        }

        private Control Block(DaySegment segment, bool dragging)
        {
            var owner = view.Owner;
            var frame = Frame(segment);
            var entry = segment.Entry;
            var catalog = owner.Catalog;
            var project = catalog.Project(entry.ProjectId);
            var color = Ui.ProjectColor(project?.Color) ?? Colors.Gray;
            var dark = ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark;
            var parts = entry.IsSplit() ? entry.Parts().OrderByDescending(p => p.Percent).ToArray() : null;
            var threaded = parts is not null && frame.Height >= 30;
            var locked = owner.Rules.IsLocked(owner.Time.Day(entry.Start));
            var suggestion = dragging || locked ? null : owner.Splits.Suggestion(entry);
            var suggested = suggestion is not null;
            var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3 };
            if (entry.IsRunning()) title.Children.Add(new Ellipse { Width = 5, Height = 5, VerticalAlignment = VerticalAlignment.Center }.Res(Shape.FillProperty, "BrandBrush"));
            if (locked) title.Children.Add(Ui.Icon("IconLockClosed16Filled", "secondary").Classed("small"));
            title.Children.Add(Ui.Text(entry.Description.Length == 0 ? "No description" : entry.Description, "caption", "medium", entry.Description.Length == 0 ? "secondary" : ""));
            var lines = Ui.Stack(1, title);
            if (frame.Height > 40)
            {
                var line = new TextBlock { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
                if (parts is null)
                {
                    line.Text = project?.Name ?? "No project";
                    line.FontWeight = FontWeight.Medium;
                    if (Ui.ReadableBrush(project?.Color, dark) is { } brush) line.Foreground = brush;
                    else line.Classes.Add("secondary");
                }
                else
                {
                    line.Text = string.Join(" · ", parts.Select(p => $"{catalog.Project(p.ProjectId)?.Name ?? "No project"} {p.Percent}%"));
                    line.Classes.Add("secondary");
                }
                lines.Children.Add(line);
            }
            if (frame.Height > 52) lines.Children.Add(Ui.Text(owner.Time.Span(entry.Start, entry.Stop), "caption", "secondary", "mono"));
            var body = new Grid();
            body.Children.Add(lines);
            if (threaded)
            {
                var thread = SplitViews.Thread(catalog, parts!);
                thread.VerticalAlignment = VerticalAlignment.Bottom;
                thread.Margin = new Thickness(0, 0, 0, 2);
                body.Children.Add(thread);
            }
            var padding = new Thickness(6, frame.Height < 24 ? 0 : 3, 6, threaded ? 4 : 2);
            if (suggestion is not null && frame.Width > 40) Preview(segment, suggestion, frame, padding, dark, body);
            var block = new Border
            {
                Width = frame.Width,
                Height = frame.Height,
                CornerRadius = new CornerRadius(5),
                Background = new SolidColorBrush(color, dragging ? 0.32 : dark ? 0.22 : 0.16),
                BorderBrush = new SolidColorBrush(color, dragging ? 1 : 0.35),
                BorderThickness = new Thickness(dragging ? 2 : 1),
                Padding = padding,
                Child = body,
                ClipToBounds = true,
                Tag = segment,
                Cursor = new Cursor(CalendarDrag.CanChange(entry, owner.Rules, owner.Time) ? StandardCursorType.SizeAll : StandardCursorType.Hand),
            }.Spoken($"{(entry.Description.Length == 0 ? "No description" : entry.Description)}, {(parts is null ? project?.Name ?? "no project" : "split: " + catalog.Describe(parts))}, {owner.Time.Span(entry.Start, entry.Stop)}{(suggested ? ", split suggested" : "")}");
            if (dragging) block.BoxShadow = BoxShadows.Parse("0 2 8 0 #33000000");
            SetLeft(block, frame.X);
            SetTop(block, frame.Y);
            return block;
        }

        /// <summary>
        /// The ✦ of a block agents suggest splitting. Hovering or focusing it draws the suggestion on the block, and only then;
        /// a click or Enter takes it, with Undo, or opens the editor when one click can't (a repository without a project, details the
        /// workspace needs).
        /// </summary>
        private void Preview(DaySegment segment, SplitSuggestion suggestion, Rect frame, Thickness padding, bool dark, Grid body)
        {
            var owner = view.Owner;
            var entry = segment.Entry;
            var time = owner.Time;
            // The part of the entry this block shows: one day of an entry over midnight.
            var from = segment.ContinuesBefore ? time.StartOf(segment.Day).ToUnixTimeMilliseconds() : suggestion.Start;
            var to = segment.ContinuesAfter ? time.StartOf(segment.Day.AddDays(1)).ToUnixTimeMilliseconds() : suggestion.End;
            var size = new Size(frame.Width - 2, frame.Height - 2);
            var (parts, dividers) = SuggestionPreview.Layout(suggestion, entry, Math.Max(from, suggestion.Start), Math.Min(to, suggestion.End), size);
            // The block's own words, which the labels keep clear of.
            var lines = body.Children[0];
            lines.Measure(new Size(Math.Max(0, frame.Width - padding.Left - padding.Right - 2), double.PositiveInfinity));
            var text = new Rect(padding.Left - 1, padding.Top - 1, lines.DesiredSize.Width, lines.DesiredSize.Height);
            // Clear of the scroll bar, which lies over the right edge of the last day.
            var underScrollBar = Math.Max(0, frame.Right - (Width0 - view.ColumnWidth - ScrollBarRoom));
            var bleed = new Thickness(-padding.Left, -padding.Top, -padding.Right, -padding.Bottom);
            Control fill = new SuggestionPreview.Fill(owner, parts, dividers, suggestion.Strength == SplitStrength.Weak, text) { Width = size.Width, Height = size.Height }.Named("CalendarSuggestionPreview");
            Control labels = SuggestionPreview.Labels(owner, suggestion, entry, parts, dividers, size, text, underScrollBar).Named("CalendarSuggestionLabels");
            foreach (var layer in new[] { fill, labels })
            {
                layer.Margin = bleed;
                layer.HorizontalAlignment = HorizontalAlignment.Left;
                layer.VerticalAlignment = VerticalAlignment.Top;
            }
            var key = (entry.Id, segment.Day);
            fill.IsVisible = labels.IsVisible = previewing is { } shown && (shown.Entry, shown.Day) == key;
            var spark = SuggestionPreview.Spark(owner, entry, suggestion, async () =>
            {
                // Taken or opened, it is no longer a preview, nor is what a redraw puts under the resting pointer.
                Unpreview();
                clickedAt = pointer;
                var item = new ReviewItem.Split(new SplitOffer(entry, suggestion));
                if (!item.IsTakeable) await owner.Splits.ReviewAsync(entry);
                else if (!await owner.Desktop.TakeAsync(item)) await owner.Desktop.ChangeAsync(item);
                view.Render();
            });
            spark.Margin = new Thickness(0, frame.Height < 24 ? -2 : -1, -4 + underScrollBar, 0);
            // A ✦ taken away by a redraw still hears the pointer and the focus leave it, later; that isn't the person.
            bool Gone() => TopLevel.GetTopLevel(spark) is null;
            void Show(bool focused)
            {
                if (Gone() || !focused && clickedAt is not null) return;
                var kept = previewing is { Focused: true } was && (was.Entry, was.Day) == key;
                Unpreview();
                previewing = (entry.Id, segment.Day, focused || kept);
                fill.IsVisible = labels.IsVisible = true;
            }
            spark.PointerEntered += (_, _) => Show(false);
            spark.PointerExited += (_, _) =>
            {
                if (Gone()) return;
                if (spark.IsKeyboardFocusWithin) previewing = (entry.Id, segment.Day, true);
                else Unpreview();
            };
            spark.GotFocus += (_, e) =>
            {
                if (e.NavigationMethod is NavigationMethod.Tab or NavigationMethod.Directional) Show(true);
            };
            spark.LostFocus += (_, _) =>
            {
                if (Gone()) return;
                if (spark.IsPointerOver) previewing = (entry.Id, segment.Day, false);
                else Unpreview();
            };
            // The proposal behind the block's words, its labels in front of them.
            body.Children.Insert(0, fill);
            body.Children.Add(labels);
            body.Children.Add(spark);
        }

        /// <summary>
        /// "✦ Atlas · 14:00–15:30": agents were at work and nothing was tracked. No fill and a hatched edge, unlike the
        /// dashed blocks of what agents proposed. A click opens a new entry for it; its menu adds one or dismisses it.
        /// </summary>
        private void Untracked()
        {
            var owner = view.Owner;
            var time = owner.Time;
            foreach (var ghost in view.Ghosts)
            {
                var index = Array.IndexOf(view.Days, ghost.Day);
                if (index < 0) continue;
                var dayStart = time.StartOf(ghost.Day);
                var from = (ghost.Start - dayStart).TotalMinutes;
                var to = (ghost.End - dayStart).TotalMinutes;
                var width = Math.Max(DayWidth - WeekReserve - 8, 4);
                var height = Math.Max((to - from) * HourHeight / 60, 14);
                var projects = ghost.Stretch.Projects.Select(id => owner.Catalog.Project(id)).OfType<ProjectResponse>().ToList();
                var names = projects.Count == 0 ? "Not tracked" : string.Join(", ", projects.Take(3).Select(p => p.Name));
                var times = $"{time.Clock(ghost.Start)}–{time.Clock(ghost.End)}";
                var title = Ui.Columns("Auto,*", 4, Ui.Icon("IconSparkle", "secondary", "small").Centered(), Ui.Text($"{names} · {times}", "caption", "medium", "secondary", "mono"));
                var lines = Ui.Stack(1, title, height > 40 ? Ui.Text("Agents at work · not tracked", "caption", "secondary") : null);
                lines.Margin = new Thickness(8, height < 24 ? 0 : 3, 8, 2);
                var block = new Panel
                {
                    Width = width, Height = height, ClipToBounds = true, Cursor = new Cursor(StandardCursorType.Hand), Background = Brushes.Transparent,
                    Children = { new HatchedEdge(projects.FirstOrDefault()?.Color), lines },
                }.Named("UntrackedGhost").Tip($"Agents were at work {times} with nothing tracked. Click to add an entry for it.")
                    .Spoken($"Not tracked: agents at work {times}{(projects.Count == 0 ? "" : " in " + names)}");
                var menu = new MenuFlyout();
                var add = new MenuItem { Header = "Add entry…", Icon = Ui.Icon("IconAdd") };
                add.Click += async (_, _) => await AddAsync(ghost);
                var dismiss = new MenuItem { Header = "Dismiss", Icon = Ui.Icon("IconDismiss") };
                dismiss.Click += (_, _) => owner.Splits.Dismiss(ghost);
                menu.Items.Add(add);
                menu.Items.Add(dismiss);
                block.ContextFlyout = menu;
                block.PointerPressed += async (_, e) =>
                {
                    if (!e.GetCurrentPoint(block).Properties.IsLeftButtonPressed) return;
                    e.Handled = true;
                    await AddAsync(ghost);
                };
                SetLeft(block, Gutter + index * DayWidth + 4);
                SetTop(block, from * HourHeight / 60);
                Children.Add(block);
            }
        }

        private async Task AddAsync(UntrackedBlock ghost)
        {
            try
            {
                await view.Owner.Splits.EditAsync(ghost);
            }
            catch (Exception exception)
            {
                view.Owner.Notify(exception.Message);
            }
            view.Render();
        }

        /// <summary>
        /// Where the time agents proposed would go, day by day, beside the entries already there: dashed, not saved, and
        /// opening the review when clicked. Over an entry it takes the right half of the day, so both can be read.
        /// </summary>
        private void Proposals()
        {
            var owner = view.Owner;
            var now = DateTimeOffset.UtcNow;
            foreach (var proposal in owner.Proposals.Pending)
            {
                var request = owner.Proposals.Request(proposal);
                if (request.Stop is not { } stop || !(proposal.IsNew || proposal.Fields.Start is not null || proposal.Fields.Stop is not null)) continue;
                var start = request.Start!.Value;
                for (var index = 0; index < view.Days.Length; index++)
                {
                    var dayStart = owner.Time.StartOf(view.Days[index]);
                    var dayEnd = owner.Time.StartOf(view.Days[index].AddDays(1));
                    if (start >= dayEnd || stop <= dayStart) continue;
                    var from = ((start > dayStart ? start : dayStart) - dayStart).TotalMinutes;
                    var to = ((stop < dayEnd ? stop : dayEnd) - dayStart).TotalMinutes;
                    var overlaps = owner.Entries.Any(e => e.Start < stop && (e.Stop ?? now) > start && e.Start < dayEnd && (e.Stop ?? now) > dayStart);
                    var column = DayWidth - WeekReserve;
                    var width = Math.Max(overlaps ? column / 2 - 4 : column - 8, 4);
                    var height = Math.Max((to - from) * HourHeight / 60, 14);
                    var description = string.IsNullOrEmpty(request.Description) ? "No description" : request.Description;
                    var title = Ui.Columns("Auto,*", 4, Ui.Icon("IconBot", "brand", "small").Centered(), Ui.Text(description, "caption", "medium"));
                    var lines = Ui.Stack(1, title, height > 40 ? Ui.Text(proposal.IsNew ? "Proposed · not saved" : "Proposed change · not applied", "caption", "secondary") : null);
                    lines.Margin = new Thickness(6, height < 24 ? 0 : 3, 6, 2);
                    var ghost = new Panel
                    {
                        Width = width, Height = height, ClipToBounds = true, Cursor = new Cursor(StandardCursorType.Hand),
                        Children = { new Border { CornerRadius = new CornerRadius(5) }.Res(Border.BackgroundProperty, "SolidCardBackgroundBrush"),
                            ProposalViews.Dashed(owner.Catalog.Project(request.ProjectId)?.Color, 5, 1.5, 0.1), lines },
                    }.Named("ProposalGhost").Tip($"Proposed by {proposal.Client ?? "an agent"}. Click to review.").Spoken($"Proposed: {description}, {owner.Time.Span(start, stop)}");
                    ghost.PointerPressed += (_, e) =>
                    {
                        e.Handled = true;
                        _ = owner.ReviewProposalsAsync();
                    };
                    SetLeft(ghost, Gutter + index * DayWidth + (overlaps ? column / 2 : 4));
                    SetTop(ghost, from * HourHeight / 60);
                    Children.Add(ghost);
                }
            }
        }

        private Control CreatePreview(CalendarDrag.Create create)
        {
            var index = Array.IndexOf(view.Days, create.Day);
            var from = Math.Min(create.Anchor, create.Current);
            var to = Math.Max(Math.Max(create.Anchor, create.Current), from + CalendarLayout.Step);
            var rect = new Border
            {
                Width = DayWidth - 8,
                Height = (to - from) * HourHeight / 60,
                CornerRadius = new CornerRadius(5),
                BorderThickness = new Thickness(1.5),
                IsHitTestVisible = false,
            }.Res(Border.BorderBrushProperty, "BrandBrush").Res(Border.BackgroundProperty, "BrandTintBrush");
            SetLeft(rect, Gutter + index * DayWidth + 4);
            SetTop(rect, from * HourHeight / 60);
            return rect;
        }

        private void NowLine()
        {
            var owner = view.Owner;
            var now = DateTimeOffset.UtcNow;
            var index = Array.IndexOf(view.Days, owner.Time.Day(now));
            if (index < 0) return;
            var y = owner.Time.MinuteOfDay(now) * HourHeight / 60;
            var dot = new Ellipse { Width = 8, Height = 8, IsHitTestVisible = false }.Res(Shape.FillProperty, "BrandBrush");
            SetLeft(dot, Gutter + index * DayWidth - 4);
            SetTop(dot, y - 4);
            var line = new Border { Width = DayWidth, Height = 2, IsHitTestVisible = false }.Res(Border.BackgroundProperty, "BrandBrush");
            SetLeft(line, Gutter + index * DayWidth);
            SetTop(line, y - 1);
            var pill = new Border { Child = Ui.Text(owner.Time.Clock(now), "caption", "strong", "mono", "onBrand"), CornerRadius = new CornerRadius(8), Padding = new Thickness(5, 0, 5, 1), IsHitTestVisible = false }
                .Res(Border.BackgroundProperty, "BrandBrush");
            pill.Width = double.NaN;
            SetLeft(pill, 2);
            SetTop(pill, y - 9);
            Children.Add(line);
            Children.Add(dot);
            Children.Add(pill);
        }

        /// <summary>Focus and agent runs: a column beside a single day, a thin strip in each day of a week.</summary>
        private void Activity()
        {
            if (!view.ShowsActivity) return;
            var owner = view.Owner;
            for (var index = 0; index < view.Days.Length; index++)
            {
                var day = view.Days[index];
                if (owner.Splits.Day(day) is not { } snapshot) continue;
                if (view.DayMode)
                {
                    var left = Width0 - view.ColumnWidth;
                    var band = new Border { Width = view.ColumnWidth, Height = Height, IsHitTestVisible = false }.Res(Border.BackgroundProperty, "ActivityBandBrush");
                    SetLeft(band, left);
                    Children.Add(band);
                    foreach (var block in FocusBlocks(snapshot.Attention)) Focus(day, block, left + 5, FocusWidth - 10, 4);
                    var formats = view.Lanes;
                    for (var lane = 0; lane < formats.Count; lane++)
                    {
                        var source = formats[lane].Source;
                        PlaceRuns(day, snapshot.Runs.Where(r => r.Source == source), left + FocusWidth + lane * LaneWidth);
                    }
                }
                else
                {
                    var x = Gutter + (index + 1) * DayWidth - WeekStrip - 2;
                    foreach (var block in FocusBlocks(snapshot.Attention)) Focus(day, block, x, WeekStrip, WeekStrip / 2, interactive: false);
                }
            }
        }

        /// <summary>Attention in stretches without a pause, each with how its time divided between repositories, in the order they first had it.</summary>
        private static List<(long Start, long End, List<(AgentRepository? Repository, long Duration)> Shares)> FocusBlocks(IReadOnlyList<AttentionSegment> attention)
        {
            var blocks = new List<(long Start, long End, List<(AgentRepository? Repository, long Duration)> Shares)>();
            foreach (var segment in attention.OrderBy(a => a.Start))
            {
                if (blocks.Count == 0 || segment.Start > blocks[^1].End + 2 * 60_000) blocks.Add((segment.Start, segment.End, []));
                var block = blocks[^1];
                var share = block.Shares.FindIndex(s => Equals(s.Repository, segment.Repository));
                if (share < 0) block.Shares.Add((segment.Repository, segment.Duration));
                else block.Shares[share] = (segment.Repository, block.Shares[share].Duration + segment.Duration);
                blocks[^1] = (block.Start, Math.Max(block.End, segment.End), block.Shares);
            }
            return blocks;
        }

        /// <summary>A focus block, banded by how its time divided between repositories.</summary>
        private void Focus(DateOnly day, (long Start, long End, List<(AgentRepository? Repository, long Duration)> Shares) block, double x, double width, double corner, bool interactive = true)
        {
            var time = view.Owner.Time;
            var from = DateTimeOffset.FromUnixTimeMilliseconds(block.Start);
            var to = DateTimeOffset.FromUnixTimeMilliseconds(block.End);
            var dayStart = time.StartOf(day);
            var dayEnd = time.StartOf(day.AddDays(1));
            if (to <= dayStart || from >= dayEnd) return;
            var top = from <= dayStart ? 0 : time.MinuteOfDay(from);
            var bottom = to >= dayEnd ? CalendarLayout.MinutesPerDay : time.MinuteOfDay(to);
            var height = Math.Max(3, (bottom - top) * HourHeight / 60);
            var total = Math.Max(1, block.Shares.Sum(s => s.Duration));
            var gap = height > block.Shares.Count * 4 ? 1 : 0;
            var bands = new StackPanel { Spacing = gap };
            var room = height - gap * (block.Shares.Count - 1);
            foreach (var share in block.Shares)
            {
                var color = Ui.ProjectColor(ActivityColors.Of(share.Repository?.Identity ?? "")) ?? Colors.Gray;
                bands.Children.Add(new Border { Height = Math.Max(1, room * share.Duration / total), Background = new SolidColorBrush(color, 0.8) });
            }
            var bar = new Border { Width = width, Height = height, CornerRadius = new CornerRadius(corner), ClipToBounds = true, Child = bands, IsHitTestVisible = interactive };
            if (interactive)
                ToolTip.SetTip(bar, $"Focus · {time.Span(from, to)}\n" + string.Join("\n", block.Shares.Select(s => $"{s.Repository?.Name ?? "Not a repository"} {Durations.HoursMinutes(s.Duration / 1000d)}")));
            SetLeft(bar, x);
            SetTop(bar, top * HourHeight / 60);
            Children.Add(bar);
        }

        /// <summary>Runs that overlap sit side by side, up to three; more share the last lane.</summary>
        private void PlaceRuns(DateOnly day, IEnumerable<AgentRun> runs, double left)
        {
            var ends = new List<long>();
            var cluster = new List<(AgentRun Run, int Lane)>();
            var clusterEnd = long.MinValue;
            void Close()
            {
                var lanes = Math.Max(1, ends.Count);
                var width = (LaneWidth - 10) / lanes;
                foreach (var (run, lane) in cluster) Bar(day, run.Start, Math.Max(run.End, run.Start + 60_000), left + 5 + lane * width, Math.Max(1, width - (lanes > 1 ? 1 : 0)), run.Repository, 0.5,
                    $"{AgentLogFormat.Of(run.Source).Label}, {run.Repository?.Name ?? "not a repository"}");
                cluster.Clear();
                ends.Clear();
            }
            foreach (var run in runs.OrderBy(r => r.Start))
            {
                if (run.Start >= clusterEnd) Close();
                var end = Math.Max(run.End, run.Start + 60_000);
                var lane = ends.FindIndex(e => e <= run.Start);
                if (lane >= 0) ends[lane] = end;
                else if (ends.Count < 3)
                {
                    lane = ends.Count;
                    ends.Add(end);
                }
                else
                {
                    lane = ends.Count - 1;
                    ends[lane] = Math.Max(ends[lane], end);
                }
                clusterEnd = Math.Max(clusterEnd, end);
                cluster.Add((run, lane));
            }
            Close();
        }

        private void Bar(DateOnly day, long start, long end, double x, double width, AgentRepository? repository, double opacity, string? tip, double corner = 4)
        {
            var time = view.Owner.Time;
            var from = DateTimeOffset.FromUnixTimeMilliseconds(start);
            var to = DateTimeOffset.FromUnixTimeMilliseconds(end);
            var dayStart = time.StartOf(day);
            var dayEnd = time.StartOf(day.AddDays(1));
            if (to <= dayStart || from >= dayEnd) return;
            var top = from <= dayStart ? 0 : time.MinuteOfDay(from);
            var bottom = to >= dayEnd ? CalendarLayout.MinutesPerDay : time.MinuteOfDay(to);
            var color = Ui.ProjectColor(ActivityColors.Of(repository?.Identity ?? "")) ?? Colors.Gray;
            var bar = new Border
            {
                Width = width,
                Height = Math.Max(3, (bottom - top) * HourHeight / 60),
                CornerRadius = new CornerRadius(corner),
                Background = new SolidColorBrush(color, opacity),
                IsHitTestVisible = tip is not null,
            };
            if (tip is not null) ToolTip.SetTip(bar, $"{tip}\n{time.Span(from, to)} · {Durations.HoursMinutes((to - from).TotalSeconds)}");
            SetLeft(bar, x);
            SetTop(bar, top * HourHeight / 60);
            Children.Add(bar);
        }

        // Dragging

        private (DateOnly Day, double Minute) Slot(Point point)
        {
            var index = Math.Clamp((int)((point.X - Gutter) / DayWidth), 0, view.Days.Length - 1);
            return (view.Days[index], Math.Clamp(point.Y * 60 / HourHeight, 0, CalendarLayout.MinutesPerDay));
        }

        private (DaySegment? Segment, bool? StartEdge) Hit(Point point)
        {
            foreach (var segment in view.Segments.Reverse())
            {
                var frame = Frame(segment);
                if (!frame.Contains(point)) continue;
                var resizable = CalendarDrag.CanChange(segment.Entry, view.Owner.Rules, view.Owner.Time);
                if (resizable && !segment.ContinuesBefore && point.Y - frame.Y < 6) return (segment, true);
                if (resizable && !segment.ContinuesAfter && frame.Bottom - point.Y < 6) return (segment, false);
                return (segment, null);
            }
            return (null, null);
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            // A press on a ✦ is its click, not the start of moving the entry.
            if (e.Handled || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            var point = e.GetPosition(this);
            if (point.X < Gutter || point.X > Width0 - view.ColumnWidth) return;
            pressedAt = point;
            var (segment, edge) = Hit(point);
            var (day, minute) = Slot(point);
            drag = CalendarDrag.Begin(segment, edge, day, minute, view.Owner.Rules, view.Owner.Time);
            if (drag is null) return;
            e.Pointer.Capture(this);
            e.Handled = true;
            Rebuild();
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            var point = e.GetPosition(this);
            if (drag is null)
            {
                var (segment, edge) = Hit(point);
                Cursor = new Cursor(edge is not null ? StandardCursorType.SizeNorthSouth : segment is null && point.X > Gutter ? StandardCursorType.Cross : StandardCursorType.Arrow);
                return;
            }
            var (day, minute) = Slot(point);
            var distance = Math.Sqrt(Math.Pow(point.X - pressedAt.X, 2) + Math.Pow(point.Y - pressedAt.Y, 2));
            drag = drag.To(day, minute, distance, view.Owner.Rules);
            Rebuild();
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            window = TopLevel.GetTopLevel(this);
            window?.AddHandler(PointerMovedEvent, PointerMovedAnywhere, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            window?.RemoveHandler(PointerMovedEvent, PointerMovedAnywhere);
            window = null;
            base.OnDetachedFromVisualTree(e);
        }

        /// <summary>
        /// A ✦ drawn anew under a resting pointer never heard it arrive, so it can't hear it leave either: the window
        /// says where the pointer went.
        /// </summary>
        private void PointerMovedAnywhere(object? sender, PointerEventArgs e)
        {
            pointer = e.GetPosition(window);
            if (clickedAt is { } at && Point.Distance(at, pointer) > 2) clickedAt = null;
            if (previewing is { Focused: false } && (e.Source as Visual)?.FindAncestorOfType<Button>(includeSelf: true) is not { Name: "CalendarSuggestion" }) Unpreview();
        }

        private void Unpreview()
        {
            previewing = null;
            foreach (var overlay in this.GetLogicalDescendants().OfType<Control>().Where(c => c.Name is "CalendarSuggestionPreview" or "CalendarSuggestionLabels")) overlay.IsVisible = false;
        }

        protected override async void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            if (drag is not { } done) return;
            drag = null;
            e.Pointer.Capture(null);
            Rebuild();
            await view.FinishAsync(done);
        }
    }

    /// <summary>Hour lines, weekend and locked days, and the hours along the side.</summary>
    private sealed class CalendarBackground(CalendarView view) : Control
    {
        public override void Render(DrawingContext context)
        {
            var width = Bounds.Width;
            var days = view.Days;
            var dayWidth = Math.Max(10, (width - Gutter - view.ColumnWidth) / days.Length);
            IBrush Resource(string key) => this.FindResource(ActualThemeVariant, key) as IBrush ?? Brushes.Gray;
            for (var index = 0; index < days.Length; index++)
            {
                var day = days[index];
                var fill = view.Owner.Rules.IsLocked(day) ? Resource("CalendarLockedBrush") : day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ? Resource("CalendarWeekendBrush") : null;
                if (fill is not null) context.FillRectangle(fill, new Rect(Gutter + index * dayWidth, 0, dayWidth, 24 * HourHeight));
            }
            var hour = new Pen(Resource("CalendarHourLineBrush"), 1);
            var half = new Pen(Resource("CalendarHalfHourLineBrush"), 1);
            for (var line = 1; line < 48; line++)
            {
                var y = Math.Round(line * HourHeight / 2) + 0.5;
                context.DrawLine(line % 2 == 0 ? hour : half, new Point(Gutter, y), new Point(width, y));
            }
            for (var index = 0; index <= days.Length; index++)
            {
                var x = Math.Round(Gutter + index * dayWidth) + 0.5;
                context.DrawLine(hour, new Point(x, 0), new Point(x, 24 * HourHeight));
            }
            var time = view.Owner.Time;
            var nowMinute = days.Contains(time.Today()) ? time.MinuteOfDay(DateTimeOffset.UtcNow) : -100;
            var text = Resource("TextSecondaryBrush");
            var typeface = new Typeface(this.FindResource("SpindleFontFamily") as FontFamily ?? FontFamily.Default);
            for (var h = 1; h < 24; h++)
            {
                if (Math.Abs(h * 60 - nowMinute) <= 20) continue;
                var label = new FormattedText(new ClockTime(h, 0).Field(time.Hour12).Replace(":00 ", " ").Replace(":00", ":00"), System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 11, text);
                context.DrawText(label, new Point(Gutter - 8 - label.Width, h * HourHeight - label.Height / 2));
            }
        }
    }
}
