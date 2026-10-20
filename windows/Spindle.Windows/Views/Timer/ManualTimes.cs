using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Spindle.Core;
using Spindle.Windows.Model;

namespace Spindle.Windows.Views;

/// <summary>The day, start and end for adding time that wasn't tracked with the timer, in the timer bar.</summary>
public sealed class ManualTimes : UserControl
{
    private readonly MainWindow owner;
    private readonly TimerBar bar;
    private readonly DayField day;
    private readonly ClockField start;
    private readonly ClockField end;
    private readonly TextBox duration = new TextBox { Width = 84, HorizontalContentAlignment = HorizontalAlignment.Right, PlaceholderText = "Duration" }.Classed("mono").Named("ManualDuration");

    public ManualTimes(MainWindow owner, TimerBar bar)
    {
        this.owner = owner;
        this.bar = bar;
        var time = owner.Time;
        var now = DateTimeOffset.UtcNow;
        day = new DayField(time.Today(), owner.Rules.FirstOpenDay);
        start = new ClockField(time, time.ClockField(now.AddHours(-1))).Named("ManualStart").Spoken("Start time");
        end = new ClockField(time, time.ClockField(now)).Named("ManualEnd").Spoken("End time");
        start.Committed += _ => ShowDuration();
        end.Committed += _ => ShowDuration();
        end.Submitted += () => _ = AddSafelyAsync();
        duration.LostFocus += (_, _) => ApplyDuration();
        duration.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            ApplyDuration();
            e.Handled = true;
        };
        var add = Ui.Button("Add", AddAsync, owner.Notify, "primary").Named("AddManual");
        Content = Ui.Row(6, day, start, Ui.Text("–", "secondary").Centered(), end, duration, add);
        ShowDuration();
    }

    /// <summary>The instants for the day and the two clock times; an end before the start ran past midnight.</summary>
    private (DateTimeOffset Start, DateTimeOffset End)? Range()
    {
        if (start.Clock is not { } from || end.Clock is not { } to) return null;
        var first = owner.Time.At(day.Day, from);
        var last = owner.Time.At(day.Day, to);
        if (last < first) last = owner.Time.At(day.Day.AddDays(1), to);
        return (first, last);
    }

    private void ShowDuration()
    {
        if (!duration.IsFocused) duration.Text = Range() is { } range ? Durations.Clock((range.End - range.Start).TotalSeconds) : "";
    }

    /// <summary>A duration typed in moves the end.</summary>
    private void ApplyDuration()
    {
        if (Durations.Parse(duration.Text ?? "") is { } seconds && Range() is { } range) end.Text = owner.Time.ClockField(range.Start.AddSeconds(seconds));
        ShowDuration();
    }

    private async Task AddSafelyAsync()
    {
        try
        {
            await AddAsync();
        }
        catch (Exception exception)
        {
            owner.Notify(exception.Message);
        }
    }

    private async Task AddAsync()
    {
        start.Commit();
        end.Commit();
        if (Range() is not { } range) throw new InvalidOperationException("Enter a start and an end time, such as 9:00 and 10:30.");
        if (owner.Rules.IsLocked(day.Day)) throw new InvalidOperationException(owner.Rules.LockMessage(owner.Time));
        var draft = bar.Draft;
        var description = bar.DraftDescription;
        var project = owner.Catalog.Project(draft.ProjectId);
        // What is missing is shown one thing at a time, in the order the fields appear in the bar.
        var missing = owner.Rules.Missing(description, draft.ProjectId, draft.TaskId, draft.TagIds, false, project);
        if (missing.Count > 0) throw new InvalidOperationException(missing[0].Message);
        var usedEnd = end.Text;
        await owner.MutateAsync(() => owner.CreateEntryAsync(new CreateTimeEntryRequest
        {
            Id = Guid.NewGuid(),
            Description = description,
            ProjectId = draft.ProjectId,
            TaskId = draft.TaskId,
            TagIds = draft.TagIds,
            IsBillable = draft.IsBillable ?? project?.IsBillable ?? false,
            Start = range.Start,
            Stop = range.End,
        }));
        // The next entry usually starts where this one ended.
        start.Text = usedEnd;
        end.Text = owner.Time.ClockField(DateTimeOffset.UtcNow);
        ShowDuration();
        bar.Clear(draft, description);
    }
}
