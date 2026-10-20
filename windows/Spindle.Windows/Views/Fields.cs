using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using Spindle.Windows.Model;

namespace Spindle.Windows.Views;

/// <summary>
/// The field every clock time is typed in, so they all work alike, as on the Mac and the web:
/// entering it selects the time; it reads 930, 9:30, 9.30, 9pm, now, or -15 for 15 minutes ago and tidies that to
/// 09:30 when left; ↑ and ↓ change it by a minute, with Shift by 15; a time it can't read turns red.
/// </summary>
public sealed class ClockField : TextBox
{
    private readonly UserTime time;

    public ClockField(UserTime time, string text = "")
    {
        this.time = time;
        Text = text;
        PlaceholderText = time.Hour12 ? "9:30 am" : "9:30";
        Width = time.Hour12 ? 92 : 72;
        HorizontalContentAlignment = HorizontalAlignment.Center;
        Classes.Add("mono");
        GotFocus += (_, _) => Dispatcher.UIThread.Post(SelectAll);
        LostFocus += (_, _) => Commit();
        TextChanged += (_, _) => Classes.Remove("invalid");
    }

    protected override Type StyleKeyOverride => typeof(TextBox);

    /// <summary>The field was left or Return was pressed: the time typed, or null if it isn't one.</summary>
    public event Action<ClockTime?>? Committed;

    /// <summary>Return was pressed, after the time was read.</summary>
    public event Action? Submitted;

    public ClockTime? Clock => time.ReadClockField(Text ?? "");

    public void Commit()
    {
        var typed = Text ?? "";
        var clock = time.ReadClockField(typed);
        if (clock is { } value) Text = value.Field(time.Hour12);
        if (clock is null && typed.Trim().Length > 0) Classes.Add("invalid");
        else Classes.Remove("invalid");
        Committed?.Invoke(clock);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        var step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 15 : 1;
        switch (e.Key)
        {
            case Key.Up:
                Nudge(step);
                e.Handled = true;
                return;
            case Key.Down:
                Nudge(-step);
                e.Handled = true;
                return;
            case Key.Enter:
                Commit();
                Submitted?.Invoke();
                e.Handled = true;
                return;
        }
        base.OnKeyDown(e);
    }

    private void Nudge(int minutes)
    {
        if (Clock is not { } clock) return;
        Text = clock.Adding(minutes).Field(time.Hour12);
        SelectAll();
    }
}

/// <summary>
/// A day typed like the times beside it and written as the PC's region writes dates; the arrow keys move it a day and
/// the calendar button opens a picker. Days before <see cref="Earliest"/>, e.g. locked ones, can't be chosen.
/// </summary>
public sealed class DayField : UserControl
{
    private readonly TextBox text = new TextBox { Width = 112 }.Classed("mono");
    private DateOnly day;

    public DayField(DateOnly day, DateOnly? earliest = null)
    {
        this.day = day;
        Earliest = earliest;
        text.Text = UserTime.DayField(day);
        text.LostFocus += (_, _) => CommitText();
        text.KeyDown += OnKey;
        text.GotFocus += (_, _) => Dispatcher.UIThread.Post(text.SelectAll);
        var calendar = new Calendar { SelectionMode = CalendarSelectionMode.SingleDate };
        var pick = new Button { Content = Ui.Icon("IconCalendarLtr"), Flyout = new Flyout { Content = calendar } }.Classed("icon").Tip("Choose a day").Spoken("Choose a day");
        calendar.SelectedDatesChanged += (_, _) =>
        {
            if (calendar.SelectedDate is { } chosen)
            {
                Set(DateOnly.FromDateTime(chosen));
                pick.Flyout?.Hide();
            }
        };
        pick.Click += (_, _) =>
        {
            calendar.DisplayDate = Day.ToDateTime(TimeOnly.MinValue);
            calendar.SelectedDate = Day.ToDateTime(TimeOnly.MinValue);
        };
        Content = Ui.Row(2, text, pick);
    }

    public DateOnly? Earliest { get; set; }
    public DateOnly Day => day;
    public event Action<DateOnly>? Changed;

    public void Set(DateOnly value)
    {
        var allowed = Earliest is { } first && value < first ? first : value;
        text.Text = UserTime.DayField(allowed);
        if (allowed == day) return;
        day = allowed;
        Changed?.Invoke(day);
    }

    private void CommitText()
    {
        if (UserTime.ParseDayField(text.Text ?? "", day) is { } typed) Set(typed);
        else text.Text = UserTime.DayField(day);
    }

    private void OnKey(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Up or Key.Down or Key.Enter)) return;
        CommitText();
        if (e.Key == Key.Up) Set(day.AddDays(1));
        if (e.Key == Key.Down) Set(day.AddDays(-1));
        e.Handled = true;
    }
}

/// <summary>A row of exclusive choices drawn as one control, as Windows 11's segmented buttons.</summary>
public sealed class Segmented : Border
{
    private readonly List<Button> buttons = [];

    public Segmented(IReadOnlyList<(object Content, string Label)> choices, int selected, Action<int> onChange)
    {
        Classes.Add("segmented");
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        for (var index = 0; index < choices.Count; index++)
        {
            var position = index;
            var button = new Button { Content = choices[index].Content }.Classed("segment").Spoken(choices[index].Label).Tip(choices[index].Content is string ? null : choices[index].Label);
            button.Click += (_, _) =>
            {
                Select(position);
                onChange(position);
            };
            buttons.Add(button);
            row.Children.Add(button);
        }
        Child = row;
        Select(selected);
    }

    public int Selected { get; private set; }

    public void Select(int index)
    {
        Selected = index;
        for (var position = 0; position < buttons.Count; position++)
        {
            if (position == index) buttons[position].Classes.Add("selected");
            else buttons[position].Classes.Remove("selected");
        }
    }
}

/// <summary>A label above a control, with an error or a hint under it, as forms are laid out on the Mac.</summary>
public sealed class FormField : StackPanel
{
    private readonly TextBlock note = Ui.Wrapped("", "caption");
    private readonly string? hint;

    public FormField(string label, Control input, string? hint = null)
    {
        Spacing = 6;
        this.hint = hint;
        Children.Add(Ui.Text(label, "medium").Named("Label"));
        Children.Add(input);
        Children.Add(note);
        Error = null;
    }

    public string? Error
    {
        get => note.Classes.Contains("critical") ? note.Text : null;
        set
        {
            note.Text = value ?? hint;
            note.IsVisible = note.Text is { Length: > 0 };
            note.Classes.Remove("critical");
            note.Classes.Remove("secondary");
            note.Classes.Add(value is null ? "secondary" : "critical");
        }
    }
}
