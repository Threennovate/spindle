using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using Spindle.Core;
using Spindle.Windows.Model;

namespace Spindle.Windows.Views;

/// <summary>Corrects when the running timer started, from the clock in the timer bar, as on the Mac.</summary>
public sealed class RunningStart : UserControl
{
    private readonly MainWindow owner;
    private readonly TimeEntryResponse entry;
    private readonly ClockField field;
    private readonly TextBlock info = Ui.Text("", "secondary", "mono");
    private readonly TextBlock message = Ui.Wrapped("", "caption");
    private readonly Button save;
    private readonly DispatcherTimer ticking = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool confirming;

    private RunningStart(MainWindow owner, TimeEntryResponse entry, Action close)
    {
        this.owner = owner;
        this.entry = entry;
        Width = 310;
        field = new ClockField(owner.Time, owner.Time.ClockField(entry.Start)).Named("RunningStartField");
        field.Width = 110;
        field.TextChanged += (_, _) =>
        {
            confirming = false;
            Show();
        };
        save = Ui.Button("Save", () => SaveAsync(close), _ => { }, "primary");
        field.Submitted += async () => await SaveAsync(close);
        var suggestions = new WrapPanel { Orientation = Orientation.Horizontal };
        if (Previous() is { } previous && previous.Stop is { } ended && DateTimeOffset.UtcNow - ended < TimeSpan.FromHours(24))
        {
            var last = Suggestion($"Last entry ended · {owner.Time.Clock(ended)}", owner.Time.ClockField(ended));
            ToolTip.SetTip(last, previous.Description.Length == 0 ? "When the previous entry ended" : $"When “{previous.Description}” ended");
            suggestions.Children.Add(last);
        }
        foreach (var minutes in new[] { 5, 15, 30 }) suggestions.Children.Add(Suggestion($"{minutes} min ago", $"-{minutes}"));
        var hint = Ui.Wrapped("Type 930, 9:30 or -15 for 15 min ago. ↑ ↓ change it by a minute, with Shift by 15.", "caption", "secondary");
        Content = Ui.Stack(10, Ui.Text("Started at", "medium"), Ui.Row(8, field, save), info, message, suggestions, hint);
        Padding = new Thickness(16);
        ticking.Tick += (_, _) => Show();
        AttachedToVisualTree += (_, _) =>
        {
            ticking.Start();
            Dispatcher.UIThread.Post(() => field.Focus());
        };
        DetachedFromVisualTree += (_, _) => ticking.Stop();
        Show();
    }

    public static void Show(Control anchor, MainWindow owner, TimeEntryResponse entry)
    {
        var flyout = new Flyout { Placement = PlacementMode.BottomEdgeAlignedRight };
        flyout.Content = new RunningStart(owner, entry, flyout.Hide);
        flyout.ShowAt(anchor);
    }

    private Button Suggestion(string label, string value)
    {
        var button = new Button { Content = label, Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(8, 2), MinHeight = 26, FontSize = 12 };
        button.Click += (_, _) =>
        {
            field.Text = value;
            field.Focus();
        };
        return button;
    }

    /// <summary>The latest entry that ended before now, other than this one.</summary>
    private TimeEntryResponse? Previous() => owner.Entries
        .Where(e => e.Id != entry.Id && e.Stop is { } stop && stop <= DateTimeOffset.UtcNow)
        .MaxBy(e => e.Stop);

    /// <summary>What was typed: <c>-15</c> is 15 minutes ago, a clock time the latest such instant; the same minute keeps the second.</summary>
    private DateTimeOffset? Typed()
    {
        if (owner.Time.ReadStartField(field.Text ?? "") is not { } typed) return null;
        return Math.Abs((typed - entry.Start).TotalSeconds) < 60 && owner.Time.ClockOf(typed) == owner.Time.ClockOf(entry.Start) ? entry.Start : typed;
    }

    private void Show()
    {
        var now = DateTimeOffset.UtcNow;
        var start = Typed() ?? entry.Start;
        var day = owner.Time.Day(start);
        var heading = day == owner.Time.Today() ? "Today" : day == owner.Time.Today().AddDays(-1) ? "Yesterday" : UserTime.Format(day, "ddd, d MMM");
        info.Text = $"{heading}, {owner.Time.Clock(start)} · {Durations.Clock((now - start).TotalSeconds)}";
        message.Classes.Remove("critical");
        message.Classes.Remove("caution");
        save.Content = confirming ? "Save Anyway" : "Save";
        save.Classes.Set("primary", !confirming);
        save.Classes.Set("dangerFilled", confirming);
        if (owner.Rules.IsLocked(day))
        {
            message.Text = owner.Rules.LockMessage(owner.Time);
            message.Classes.Add("critical");
        }
        else if ((now - start).TotalHours >= 12)
        {
            message.Text = "The timer would have been running for over 12 hours." + (confirming ? " Save anyway to keep it." : "");
            message.Classes.Add("caution");
        }
        else if (Previous() is { Stop: { } ended } previous && (ended - start).TotalSeconds >= 60)
        {
            message.Text = $"Overlaps {(previous.Description.Length == 0 ? "the previous entry" : $"“{previous.Description}”")}, which ended at {owner.Time.Clock(ended)}.";
            message.Classes.Add("caution");
        }
        else
        {
            message.Text = "";
        }
        message.IsVisible = message.Text.Length > 0;
        save.IsEnabled = !owner.Rules.IsLocked(day);
    }

    private async Task SaveAsync(Action close)
    {
        if (Typed() is not { } start)
        {
            message.Text = "Enter a time such as 930, 9:30 or -15 for 15 minutes ago.";
            message.Classes.Add("critical");
            message.IsVisible = true;
            return;
        }
        if (owner.Rules.IsLocked(owner.Time.Day(start))) return;
        if ((DateTimeOffset.UtcNow - start).TotalHours >= 12 && !confirming)
        {
            confirming = true;
            Show();
            return;
        }
        if (start == entry.Start)
        {
            close();
            return;
        }
        try
        {
            await owner.UpdateRunningAsync(r => r with { Start = start });
            close();
        }
        catch (Exception exception)
        {
            message.Text = exception.Message;
            message.Classes.Add("critical");
            message.IsVisible = true;
        }
    }
}
