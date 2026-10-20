using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using Spindle.Core;
using Spindle.Windows.Model;

namespace Spindle.Windows.Views;

/// <summary>What the next entry will be, before it starts: the timer bar's draft.</summary>
public sealed record EntryDraft
{
    public Guid? ProjectId { get; init; }
    public Guid? TaskId { get; init; }
    public Guid[] TagIds { get; init; } = [];
    /// <summary>Null follows the project's billable default.</summary>
    public bool? IsBillable { get; init; }
}

/// <summary>
/// The bar at the top of the timer: start and stop the timer, or add time manually. The running entry's description,
/// project, tags and billable flag are changed right here, as on the Mac.
/// </summary>
public sealed class TimerBar : UserControl
{
    private readonly MainWindow owner;
    private readonly DescriptionField description;
    private readonly TextBox runningDescription = new TextBox { PlaceholderText = "Add description", MaxLength = 3000, Name = "RunningDescription" }.Classed("bare", "large");
    private readonly ContentControl fields = new();
    private readonly ContentControl trailing = new();
    private readonly ContentControl lead = new();
    private readonly TextBlock clock = Ui.Text("0:00:00", "clock").Named("TimerClock");
    private readonly Segmented mode;
    private Guid? shownRunning;
    private ManualTimes? manual;

    public TimerBar(MainWindow owner)
    {
        this.owner = owner;
        description = new DescriptionField(owner);
        description.Submitted += () =>
        {
            if (!Manual) _ = StartSafelyAsync();
        };
        description.Picked += suggestion => _ = Run(() => owner.StartAsync(suggestion.Starting()), cleared: true);
        runningDescription.VerticalAlignment = VerticalAlignment.Center;
        runningDescription.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                SaveRunningDescription();
                TopLevel.GetTopLevel(this)?.Focus();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                runningDescription.Text = owner.Running?.Description ?? "";
                TopLevel.GetTopLevel(this)?.Focus();
                e.Handled = true;
            }
        };
        runningDescription.LostFocus += (_, _) => SaveRunningDescription();
        mode = new Segmented(
            [(Ui.Icon("IconTimer"), "Timer"), (Ui.Icon("IconTextBulletListAdd"), "Add time manually")],
            owner.Preferences.ManualEntry ? 1 : 0,
            index =>
            {
                owner.SavePreferences(owner.Preferences with { ManualEntry = index == 1 });
                Refresh();
            });
        var bar = Ui.Columns("*,Auto,Auto,Auto", 10, lead, fields.Centered(), trailing.Centered(), mode);
        Content = new Border { Child = bar }.Classed("timerBar");
        Refresh();
    }

    public EntryDraft Draft { get; private set; } = new();
    private bool Manual => owner.Running is null && mode.Selected == 1;

    public void FocusDescription() => Dispatcher.UIThread.Post(() => (owner.Running is null ? description.Box : runningDescription).Focus());

    public void Refresh()
    {
        var running = owner.Running;
        lead.Content = running is null ? description : runningDescription;
        if (running is not null && (running.Id != shownRunning || !runningDescription.IsFocused)) runningDescription.Text = running.Description;
        shownRunning = running?.Id;
        mode.IsVisible = running is null;
        fields.Content = FieldButtons(running);
        if (running is not null) trailing.Content = RunningControls(running);
        else if (Manual) trailing.Content = manual ??= new ManualTimes(owner, this);
        else trailing.Content = StartControls();
        Tick();
    }

    public void Tick()
    {
        if (owner.Running is { } running) clock.Text = Durations.Clock(running.Seconds(DateTimeOffset.UtcNow));
        if (trailing.Content is Control controls && controls.Tag is PomodoroChip chip) chip.Tick();
    }

    private Control StartControls()
    {
        var idle = Ui.Text("0:00:00", "clock", "secondary");
        idle.Width = 90;
        idle.TextAlignment = Avalonia.Media.TextAlignment.Right;
        var readOnly = owner.ReadOnlyOrganization is not null;
        var start = Ui.Button(Ui.Icon("IconPlayFilled"), StartAsync, owner.Notify, "start").Named("StartTimer").Spoken("Start timer")
            .Tip(readOnly ? "This workspace is read-only, so time can't be added or changed." : "Start timer");
        start.IsEnabled = !readOnly;
        var chip = new PomodoroChip(owner);
        return new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { chip, idle.Centered(), start }, Tag = chip };
    }

    private Control RunningControls(TimeEntryResponse running)
    {
        clock.Width = 90;
        clock.TextAlignment = Avalonia.Media.TextAlignment.Right;
        if (clock.Parent is Panel old) old.Children.Remove(clock);
        if (clock.Parent is ContentControl holder) holder.Content = null;
        var clockButton = new Button { Content = clock, Padding = new Thickness(4, 2) }.Classed("field").Tip("Change start time").Spoken("Change start time");
        clockButton.Click += (_, _) => RunningStart.Show(clockButton, owner, running);
        var stop = Ui.Button(Ui.Icon("IconStopFilled"), () => owner.StopAsync(), owner.Notify, "stop").Named("StopTimer").Spoken("Stop timer").Tip("Stop timer");
        var chip = new PomodoroChip(owner);
        var suggestion = SplitChips.ForRunning(owner, running);
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Tag = chip };
        // Redrawn with the timer bar, so it goes away on its own after the first minutes.
        if (SplitChips.EarlierStartChip(owner, running) is { } earlier) controls.Children.Add(earlier);
        controls.Children.Add(suggestion);
        controls.Children.Add(chip);
        controls.Children.Add(clockButton);
        controls.Children.Add(stop);
        return controls;
    }

    /// <summary>The project, tags and billable buttons, for the running entry or the next one.</summary>
    private Control FieldButtons(TimeEntryResponse? running)
    {
        var catalog = owner.Catalog;
        var projectId = running?.ProjectId ?? Draft.ProjectId;
        var taskId = running is null ? Draft.TaskId : running.TaskId;
        var tagIds = running?.TagIds ?? Draft.TagIds;
        var billable = running?.AnyBillable() ?? Draft.IsBillable ?? catalog.Project(Draft.ProjectId)?.IsBillable ?? false;
        Control projectButton;
        if (running is not null && running.IsSplit())
        {
            var split = new Button { Content = SplitViews.Label(catalog, running.Parts()), MaxWidth = 280 }.Classed("field").Named("TimerProject")
                .Tip($"Split: {catalog.Describe(running.Parts())}. Click to change it.");
            split.Click += async (_, _) => await owner.EditAsync(running);
            projectButton = split;
        }
        else
        {
            var project = catalog.Project(projectId);
            Control content = project is null ? Ui.Icon("IconFolder", "secondary") : SplitViews.ProjectLabel(project, catalog.Task(taskId));
            var button = new Button { Content = content, MaxWidth = 240 }.Classed("field").Named("TimerProject")
                .Tip(project is null ? "Choose project" : "Project: " + project.Name).Spoken(project is null ? "Choose project" : "Project: " + project.Name);
            button.Click += (_, _) => PickerList.Show(button, ProjectPicker.Create(owner, projectId, taskId, SetProjectAsync,
                onSplit: running is null ? null : () => owner.EditAsync(running, start: new EditorStart(StartSplit: true))));
            projectButton = button;
        }
        var tagNames = string.Join(", ", tagIds.Select(id => catalog.Tag(id)?.Name).Where(n => n is not null));
        Control tagContent = tagIds.Length == 0
            ? Ui.Icon("IconTag", "secondary")
            : Ui.Row(6, Ui.Icon("IconTag", "brand").Centered(), Ui.Text(tagNames, "brand").Centered());
        var tags = new Button { Content = tagContent, MaxWidth = 180 }.Classed("field").Named("TimerTags")
            .Tip(tagIds.Length == 0 ? "Choose tags" : "Tags: " + tagNames).Spoken(tagIds.Length == 0 ? "Choose tags" : "Tags: " + tagNames);
        tags.Click += (_, _) => PickerList.Show(tags, TagPicker.Create(owner, tagIds, SetTagsAsync));
        var money = Ui.Icon("IconMoney", billable ? "brand" : "secondary");
        var billableButton = new Button { Content = money }.Classed("field").Named("TimerBillable").Tip(billable ? "Billable" : "Non-billable").Spoken("Billable");
        if (billable) billableButton.Res(Button.BackgroundProperty, "BrandTintBrush");
        billableButton.Click += async (_, _) => await SetBillableAsync(!billable);
        return Ui.Row(2, projectButton, tags, billableButton);
    }

    internal Task ChooseProjectAsync(Guid? projectId, Guid? taskId) => SetProjectAsync(projectId, taskId);

    private async Task SetProjectAsync(Guid? projectId, Guid? taskId)
    {
        if (owner.Running is { } running)
        {
            var billable = owner.Catalog.Project(projectId)?.IsBillable ?? running.IsBillable;
            // One project chosen ends a split.
            await Run(() => owner.UpdateRunningAsync(r => r with { ProjectId = projectId, TaskId = taskId, IsBillable = billable, Shares = [] }));
            return;
        }
        Draft = Draft with { ProjectId = projectId, TaskId = taskId, IsBillable = null };
        Refresh();
    }

    private async Task SetTagsAsync(Guid[] tagIds)
    {
        if (owner.Running is not null)
        {
            await Run(() => owner.UpdateRunningAsync(r => r with { TagIds = tagIds }));
            return;
        }
        Draft = Draft with { TagIds = tagIds };
        Refresh();
    }

    /// <summary>Billable applies to every part of a split entry.</summary>
    private async Task SetBillableAsync(bool value)
    {
        if (owner.Running is not null)
        {
            await Run(() => owner.UpdateRunningAsync(r => r with { IsBillable = value, Shares = r.Shares?.Select(s => s with { IsBillable = value }).ToArray() }));
            return;
        }
        Draft = Draft with { IsBillable = value };
        Refresh();
    }

    private void SaveRunningDescription()
    {
        if (owner.Running is not { } running) return;
        var text = (runningDescription.Text ?? "").Trim();
        if (text == running.Description) return;
        _ = Run(() => owner.UpdateRunningAsync(r => r with { Description = text }));
    }

    public async Task StartAsync()
    {
        var used = Draft;
        var text = description.Text.Trim();
        await owner.StartAsync(new CreateTimeEntryRequest
        {
            Description = text,
            ProjectId = Draft.ProjectId,
            TaskId = Draft.TaskId,
            TagIds = Draft.TagIds,
            IsBillable = Draft.IsBillable ?? owner.Catalog.Project(Draft.ProjectId)?.IsBillable ?? false,
        });
        Clear(used, text);
    }

    private Task StartSafelyAsync() => Run(StartAsync);

    /// <summary>Clears the draft after it was used, unless it was changed while the request was on its way.</summary>
    public void Clear(EntryDraft used, string usedDescription)
    {
        if (Draft == used) Draft = new EntryDraft();
        if (description.Text.Trim() == usedDescription) description.Text = "";
        Refresh();
    }

    public string DraftDescription => description.Text.Trim();

    private async Task Run(Func<Task> action, bool cleared = false)
    {
        try
        {
            await action();
            if (cleared)
            {
                description.Text = "";
                Draft = new EntryDraft();
                Refresh();
            }
        }
        catch (Exception exception)
        {
            owner.Notify(exception.Message);
        }
    }
}

/// <summary>The Pomodoro's countdown: the focus left in this interval, or the break left.</summary>
public sealed class PomodoroChip : Border
{
    private readonly MainWindow owner;
    private readonly TextBlock text = Ui.Text("", "caption", "secondary", "mono");
    private readonly PathIcon icon = Ui.Icon("IconTimer", "secondary", "small");

    public PomodoroChip(MainWindow owner)
    {
        this.owner = owner;
        Classes.Add("chip");
        Child = Ui.Row(5, icon.Centered(), text.Centered());
        Padding = new Thickness(8, 3);
        Tick();
    }

    public void Tick()
    {
        var preferences = owner.Me?.Preferences;
        if (preferences is null || !preferences.PomodoroEnabled)
        {
            IsVisible = false;
            return;
        }
        var now = DateTimeOffset.UtcNow;
        if (owner.Running is { } running)
        {
            IsVisible = true;
            icon.Res(PathIcon.DataProperty, "IconTimer");
            text.Text = Pomodoro.Countdown(Pomodoro.FocusLeft((now - running.Start).TotalSeconds, preferences.PomodoroMinutes));
            ToolTip.SetTip(this, "Focus left in this interval");
        }
        else if (owner.BreakEnds is { } ends && ends > now)
        {
            IsVisible = true;
            icon.Res(PathIcon.DataProperty, "IconDrinkCoffee");
            text.Text = "Break " + Pomodoro.Countdown((ends - now).TotalSeconds);
            ToolTip.SetTip(this, "Break left");
        }
        else
        {
            IsVisible = false;
        }
    }
}
