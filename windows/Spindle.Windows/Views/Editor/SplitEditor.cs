using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Spindle.Windows.Model;

namespace Spindle.Windows.Views;

/// <summary>
/// Splits an entry's time between projects: a bar whose dividers are dragged, and a row per part where its project,
/// share, time and billable flag are set, and its own description when the parts are described one by one. Removing
/// all but one part ends the split. As on the Mac.
/// </summary>
public sealed class SplitEditor : UserControl
{
    private readonly MainWindow owner;
    private readonly Func<double> seconds;
    private readonly bool running;
    private readonly StackPanel rows = new() { Spacing = 2 };
    private readonly SplitBar bar;
    private readonly TextBlock error = Ui.Wrapped("", "critical", "caption");
    private readonly Button add;
    private readonly Button equal;
    private readonly Button oneDescription;
    private readonly List<TextBox> durations = [];
    private readonly Dictionary<int, TextBox> descriptions = [];

    public SplitEditor(MainWindow owner, IReadOnlyList<SplitPart> parts, Func<double> seconds, bool running)
    {
        this.owner = owner;
        this.seconds = seconds;
        this.running = running;
        Parts = parts.ToList();
        bar = new SplitBar(owner.Catalog, percents => SetPercents(percents));
        add = new Button { Content = Ui.Labeled("IconAdd", "Add project") }.Named("AddSplitProject");
        add.Click += (_, _) => PickerList.Show(add, ProjectPicker.Create(owner, null, null, (project, task) =>
        {
            var next = SplitShares.Adding(Parts.Select(p => p.Percent).ToArray());
            for (var index = 0; index < Parts.Count; index++) Parts[index] = Parts[index] with { Percent = next[index] };
            Parts.Add(new SplitPart(project, task, owner.Catalog.Project(project)?.IsBillable ?? false, next[^1], DescribesEach ? "" : null));
            Render();
            return Task.CompletedTask;
        }, Taken(null), choosingAnew: true, placeholder: "Add which project to the split?"));
        equal = Ui.Button("Split equally", () => SetPercents(SplitShares.Equal(Parts.Count)), "link");
        oneDescription = Ui.Button("One description for all", () => DescribedAsOne?.Invoke(), "link").Named("SplitOneDescription")
            .Tip("Show one description on every project, timesheet links included");
        var actions = Ui.Columns("Auto,*,Auto,Auto", 8, add, null, oneDescription.Margin(0, 0, 12, 0).Centered(), equal.Centered());
        Content = Ui.Stack(10, bar, rows, actions, error);
        Render();
    }

    public List<SplitPart> Parts { get; }

    /// <summary>Whether each part has a description of its own, in place of the entry's one.</summary>
    public bool DescribesEach => SplitShares.DescribesEach(Parts);

    /// <summary>A split left with one part ends; the editor shows that part's project again.</summary>
    public event Action<SplitPart>? Ended;

    /// <summary>"One description for all": the parts go back to sharing the entry's description.</summary>
    public event Action? DescribedAsOne;

    /// <summary>The parts were drawn again, e.g. one was added or removed.</summary>
    public event Action? Rendered;

    /// <summary>Puts the keyboard in a part's own description, once it is drawn.</summary>
    public void FocusDescription(int index) => Dispatcher.UIThread.Post(() =>
    {
        if (!descriptions.TryGetValue(index, out var box)) return;
        box.Focus();
        box.CaretIndex = box.Text?.Length ?? 0;
    });

    public string? Error
    {
        get => error.Text;
        set
        {
            error.Text = value;
            error.IsVisible = value is { Length: > 0 };
        }
    }

    public Dictionary<int, string> PartErrors { get; } = [];

    public void Render()
    {
        // A part's description keeps the keyboard while the rows are drawn again.
        var typing = descriptions.FirstOrDefault(d => d.Value.IsFocused).Value is { } focused ? descriptions.First(d => d.Value == focused).Key : (int?)null;
        bar.Show(Parts);
        rows.Children.Clear();
        durations.Clear();
        descriptions.Clear();
        var minutes = Minutes();
        for (var index = 0; index < Parts.Count; index++)
        {
            rows.Children.Add(Row(index, minutes[index] * 60));
            if (Parts[index].Description is not null)
            {
                var part = index;
                rows.Children.Add(DescriptionCompletion.Attach(owner, Description(index), () => part < Parts.Count ? Parts[part].ProjectId : null));
            }
            if (PartErrors.TryGetValue(index, out var message)) rows.Children.Add(Ui.Wrapped(message, "critical", "caption").Margin(8, 0, 0, 4));
        }
        add.IsEnabled = Parts.Count < SplitShares.MaxParts;
        equal.IsEnabled = !Parts.Select(p => p.Percent).SequenceEqual(SplitShares.Equal(Parts.Count));
        oneDescription.IsVisible = DescribesEach;
        Error = error.Text;
        if (typing is { } again) FocusDescription(again);
        Rendered?.Invoke();
    }

    private long[] Minutes() => SplitShares.Minutes((long)Math.Round(seconds() / 60), Parts.Select(p => p.Percent).ToArray());

    /// <summary>A running timer's parts grow every second; only their times are drawn again, so typing goes on undisturbed.</summary>
    public void Tick()
    {
        if (!running) return;
        var minutes = Minutes();
        for (var index = 0; index < durations.Count && index < minutes.Length; index++)
            if (!durations[index].IsFocused) durations[index].Text = Durations.ShortClock(minutes[index] * 60);
    }

    /// <summary>A part's own description, under its project, as a line of text that is edited in place.</summary>
    private TextBox Description(int index)
    {
        var name = owner.Catalog.Project(Parts[index].ProjectId)?.Name ?? "this part";
        var box = new TextBox { Text = Parts[index].Description, PlaceholderText = $"What did you do on {name}?", MaxLength = 3000 }
            .Named($"SplitDescription{index}").Spoken("Description for " + owner.Catalog.Name(Parts[index])).Margin(16, 2, 0, 8);
        box.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty && index < Parts.Count) Parts[index] = Parts[index] with { Description = box.Text ?? "" };
        };
        descriptions[index] = box;
        return box;
    }

    private void SetPercents(IReadOnlyList<int> next)
    {
        for (var index = 0; index < Parts.Count; index++) Parts[index] = Parts[index] with { Percent = next[index] };
        Render();
    }

    private Func<Guid?, Guid?, bool> Taken(int? except) => (project, task) =>
        Parts.Where((_, i) => i != except).Any(p => p.ProjectId == project && p.TaskId == task);

    private Control Row(int index, long partSeconds)
    {
        var part = Parts[index];
        var catalog = owner.Catalog;
        Control label = catalog.Project(part.ProjectId) is { } project
            ? SplitViews.ProjectLabel(project, catalog.Task(part.TaskId))
            : Ui.Row(6, new Ellipse { Width = 9, Height = 9, StrokeThickness = 1, StrokeDashArray = [2, 2] }.Res(Shape.StrokeProperty, "TextSecondaryBrush").Centered(), Ui.Text("No project", "secondary"));
        var choose = new Button { Content = label, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left }
            .Classed("field").Spoken($"Part {index + 1}: {catalog.Name(part)}");
        choose.Click += (_, _) => PickerList.Show(choose, ProjectPicker.Create(owner, part.ProjectId, part.TaskId, (projectId, taskId) =>
        {
            Parts[index] = Parts[index] with { ProjectId = projectId, TaskId = taskId, IsBillable = catalog.Project(projectId)?.IsBillable ?? part.IsBillable };
            Render();
            return Task.CompletedTask;
        }, Taken(index)));
        var percent = new TextBox { Text = part.Percent.ToString(), Width = 52, HorizontalContentAlignment = HorizontalAlignment.Right }.Classed("mono").Spoken("Share of " + catalog.Name(part));
        void CommitPercent()
        {
            var digits = new string((percent.Text ?? "").Where(char.IsDigit).ToArray());
            if (int.TryParse(digits, out var typed) && typed != Parts[index].Percent) SetPercents(SplitShares.Setting(Parts.Select(p => p.Percent).ToArray(), index, typed));
            else percent.Text = Parts[index].Percent.ToString();
        }
        percent.LostFocus += (_, _) => CommitPercent();
        percent.KeyDown += (_, e) =>
        {
            var current = Parts.Select(p => p.Percent).ToArray();
            if (e.Key == Key.Enter) CommitPercent();
            else if (e.Key == Key.Up) SetPercents(SplitShares.Setting(current, index, current[index] + 1));
            else if (e.Key == Key.Down) SetPercents(SplitShares.Setting(current, index, current[index] - 1));
            else return;
            e.Handled = true;
        };
        var duration = new TextBox { Text = Durations.ShortClock(partSeconds), Width = 64, HorizontalContentAlignment = HorizontalAlignment.Right, IsEnabled = !running }
            .Classed("mono", "bare").Tip(running ? "The timer is running; set the share in percent." : "Type a duration, e.g. 1:15").Spoken("Time on " + catalog.Name(part));
        durations.Add(duration);
        void CommitDuration()
        {
            if (Durations.Parse(duration.Text ?? "") is { } typed && typed != partSeconds)
                SetPercents(SplitShares.Setting(Parts.Select(p => p.Percent).ToArray(), index, SplitShares.PercentOf(typed, (long)seconds())));
            else duration.Text = Durations.ShortClock(partSeconds);
        }
        duration.LostFocus += (_, _) => CommitDuration();
        duration.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            CommitDuration();
            e.Handled = true;
        };
        var billable = new Button { Content = Ui.Icon("IconMoney", part.IsBillable ? "brand" : "secondary") }.Classed("icon", "small")
            .Tip(part.IsBillable ? "Billable" : "Non-billable").Spoken("Billable: " + catalog.Name(part));
        if (part.IsBillable) billable.Res(Button.BackgroundProperty, "BrandTintBrush");
        billable.Click += (_, _) =>
        {
            Parts[index] = Parts[index] with { IsBillable = !Parts[index].IsBillable };
            Render();
        };
        var remove = Ui.IconButton("IconDismiss16", "Remove from the split", () =>
        {
            var next = SplitShares.Removing(Parts.Select(p => p.Percent).ToArray(), index);
            Parts.RemoveAt(index);
            for (var position = 0; position < Parts.Count; position++) Parts[position] = Parts[position] with { Percent = next[position] };
            if (Parts.Count < 2)
            {
                Ended?.Invoke(Parts[0]);
                return;
            }
            Render();
        }, "small");
        return Ui.Columns("*,Auto,Auto,Auto,Auto,Auto", 6, choose, percent.Centered(), Ui.Text("%", "secondary").Centered(), duration.Centered(), billable.Centered(), remove.Centered());
    }
}

/// <summary>
/// The parts as segments of one bar, each as wide as its share. Dragging anywhere on it moves the nearest divider,
/// snapping to 5% (1% with Alt held); double-clicking a divider evens out the parts beside it.
/// </summary>
public sealed class SplitBar : Control
{
    private const int Step = 5;
    private readonly Catalog catalog;
    private readonly Action<int[]> onPercents;
    private IReadOnlyList<SplitPart> parts = [];
    private int? dragging;

    public SplitBar(Catalog catalog, Action<int[]> onPercents)
    {
        this.catalog = catalog;
        this.onPercents = onPercents;
        Height = 40;
        Cursor = new Cursor(StandardCursorType.SizeWestEast);
        ClipToBounds = false;
    }

    public void Show(IReadOnlyList<SplitPart> next)
    {
        parts = next;
        InvalidateVisual();
    }

    private int[] Percents => parts.Select(p => p.Percent).ToArray();

    private int[] Dividers()
    {
        var percents = Percents;
        return Enumerable.Range(0, Math.Max(0, percents.Length - 1)).Select(i => percents.Take(i + 1).Sum()).ToArray();
    }

    private int Nearest(double position)
    {
        var bounds = Dividers();
        return bounds.Length == 0 ? 0 : Enumerable.Range(0, bounds.Length).MinBy(i => Math.Abs(bounds[i] - position));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        if (parts.Count < 2) return;
        var position = e.GetPosition(this).X / Bounds.Width * 100;
        dragging = Nearest(position);
        if (e.ClickCount == 2)
        {
            var percents = Percents;
            var index = dragging.Value;
            var bounds = Dividers();
            var pair = percents[index] + percents[index + 1];
            onPercents(SplitShares.MoveBoundary(percents, index, bounds[index] - percents[index] + (pair + 1) / 2));
            dragging = null;
            return;
        }
        e.Pointer.Capture(this);
        Move(e);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (dragging is not null) Move(e);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        dragging = null;
        e.Pointer.Capture(null);
        InvalidateVisual();
    }

    private void Move(PointerEventArgs e)
    {
        if (dragging is not { } index) return;
        var fine = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        onPercents(SplitShares.MoveBoundary(Percents, index, e.GetPosition(this).X / Bounds.Width * 100, fine ? 1 : Step));
    }

    public override void Render(DrawingContext context)
    {
        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width <= 0 || parts.Count == 0) return;
        var background = this.FindResource(ActualThemeVariant, "SolidCardBackgroundBrush") as IBrush ?? Brushes.White;
        var ink = this.FindResource(ActualThemeVariant, "TextPrimaryBrush") as IBrush ?? Brushes.Black;
        var secondary = this.FindResource(ActualThemeVariant, "TextTertiaryBrush") as IBrush ?? Brushes.Gray;
        using (context.PushClip(new RoundedRect(new Rect(0, 0, width, height), 6)))
        {
            var x = 0d;
            foreach (var part in parts)
            {
                var segment = width * part.Percent / 100;
                var color = Ui.ProjectColor(catalog.ColorOf(part));
                var tint = color is { } c ? new SolidColorBrush(c, 0.2) : new SolidColorBrush(Colors.Gray, 0.2);
                var solid = color is { } d ? new SolidColorBrush(d) : secondary;
                context.FillRectangle(tint, new Rect(x, 0, segment, height));
                context.FillRectangle(solid, new Rect(x, height - 3, segment, 3));
                var label = part.Percent >= 18 ? $"{catalog.Project(part.ProjectId)?.Name ?? "No project"}  {part.Percent}%" : part.Percent >= 8 ? $"{part.Percent}%" : "";
                if (label.Length > 0)
                {
                    var text = new FormattedText(label, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.Medium), 12, ink)
                    { MaxTextWidth = Math.Max(1, segment - 18), Trimming = TextTrimming.CharacterEllipsis, MaxLineCount = 1 };
                    context.DrawText(text, new Point(x + 9, (height - text.Height) / 2));
                }
                x += segment;
            }
        }
        var bounds = Dividers();
        for (var index = 0; index < bounds.Length; index++)
        {
            var at = width * bounds[index] / 100;
            context.FillRectangle(background, new Rect(at - 1, 0, 2, height));
            var handle = new RoundedRect(new Rect(at - 3.5, height / 2 - 12, 7, 24), 3.5);
            context.DrawRectangle(ink, new Pen(background, 2), handle);
            if (dragging == index)
            {
                var percents = Percents;
                var label = new FormattedText($"{percents[index]} · {percents[index + 1]}", System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.Medium), 12, background);
                var box = new Rect(at - label.Width / 2 - 6, -label.Height - 8, label.Width + 12, label.Height + 4);
                context.DrawRectangle(ink, null, new RoundedRect(box, 5));
                context.DrawText(label, new Point(box.X + 6, box.Y + 2));
            }
        }
    }
}
