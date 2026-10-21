using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using Spindle.Activity;
using Spindle.Core;
using Spindle.Windows.Model;

namespace Spindle.Windows.Views;

/// <summary>
/// How the editor opens: splitting at once, with a split or separate entries already chosen, or with what an agent
/// proposed (<see cref="Proposed"/>) in place of the entry's own values, to change before saving.
/// </summary>
public sealed record EditorStart(bool StartSplit = false, SplitPart[]? Split = null, (Guid ProjectId, DateTimeOffset Start)[]? Stretches = null, EntryDraft? Details = null, string? Description = null,
    TimeEntryRequest? Proposed = null);

/// <summary>
/// Edits an entry, or adds one for a stretch of time, in the person's own time zone: a day, a start and an end on the
/// clock, and the duration between them. Entries on locked days are shown, not edited. As the Mac's editor.
/// </summary>
public sealed class EntryEditor : UserControl
{
    private const string UnreadableTime = "Enter a time such as 930 or 9:30.";
    private readonly MainWindow owner;
    private readonly TimeEntryResponse? entry;
    private readonly bool stopping;
    private readonly bool locked;
    private readonly TextBox description = new TextBox { PlaceholderText = "Add description", MaxLength = 3000 }.Named("EditorDescription");
    private readonly FormField descriptionField;
    private readonly StackPanel descriptionArea = new() { Spacing = 6 };
    private readonly ContentControl reachSlot = new();
    private readonly ContentControl projectSlot = new();
    private readonly ContentControl tagSlot = new();
    private readonly ContentControl splitSlot = new();
    private readonly ContentControl suggestionSlot = new();
    private readonly Grid pickers = new() { ColumnSpacing = 16 };
    private readonly ToggleSwitch billable = new() { OnContent = "Billable", OffContent = "Billable", Name = "EditorBillable" };
    private readonly DayField day;
    private readonly ClockField start;
    private readonly ClockField? end;
    private readonly TextBox duration = new TextBox { Width = 96 }.Classed("mono").Named("EditorDuration");
    private readonly TextBlock timeError = Ui.Wrapped("", "critical", "caption");
    private readonly ContentControl banner = new();
    private readonly DispatcherTimer ticking = new() { Interval = TimeSpan.FromSeconds(1) };
    private Guid? projectId;
    private Guid? taskId;
    private Guid[] tagIds;
    private DateTimeOffset startValue;
    private DateTimeOffset? endValue;
    private SplitEditor? split;
    private FormField? splitField;
    private (Guid ProjectId, DateTimeOffset Start)[]? stretches;
    private FormField? projectField;
    private FormField? tagField;
    private bool saving;
    private readonly TimeEntryRequest? proposed;

    public EntryEditor(MainWindow owner, TimeEntryResponse? entry, DateTimeOffset? initialStart = null, DateTimeOffset? initialEnd = null, bool stopping = false, EditorStart? begin = null)
    {
        this.owner = owner;
        this.entry = entry;
        this.stopping = stopping && entry?.IsRunning() == true;
        var time = owner.Time;
        locked = entry is not null && owner.Rules.IsLocked(time.Day(entry.Start));
        description.Text = entry?.Description ?? begin?.Description ?? "";
        projectId = entry?.ProjectId ?? begin?.Details?.ProjectId;
        taskId = entry?.TaskId ?? begin?.Details?.TaskId;
        tagIds = entry?.TagIds ?? begin?.Details?.TagIds ?? [];
        billable.IsChecked = entry?.IsBillable ?? begin?.Details?.IsBillable ?? owner.Catalog.Project(projectId)?.IsBillable ?? false;
        var now = DateTimeOffset.UtcNow;
        startValue = entry?.Start ?? initialStart ?? Floor(now.AddHours(-1));
        endValue = entry is null ? initialEnd ?? (initialStart is { } given ? given.AddHours(1) : Floor(now)) : entry.Stop;
        proposed = begin?.Proposed;
        if (proposed is not null)
        {
            description.Text = proposed.Description ?? "";
            projectId = proposed.ProjectId;
            taskId = proposed.TaskId;
            tagIds = proposed.TagIds ?? tagIds;
            billable.IsChecked = proposed.IsBillable ?? billable.IsChecked;
            startValue = proposed.Start ?? startValue;
            endValue = proposed.Stop ?? endValue;
        }
        day = new DayField(time.Day(startValue), locked ? null : owner.Rules.FirstOpenDay);
        day.Changed += ChangeDay;
        start = new ClockField(time, time.ClockField(startValue)).Named("EditorStart").Spoken("Start");
        start.Committed += _ => CommitStart();
        if (endValue is { } stop)
        {
            end = new ClockField(time, time.ClockField(stop)).Named("EditorEnd").Spoken("End");
            end.Committed += _ => CommitEnd();
        }
        duration.LostFocus += (_, _) => CommitDuration();
        duration.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            CommitDuration();
            e.Handled = true;
        };
        duration.IsEnabled = endValue is not null;
        descriptionField = new FormField(Label("Description", owner.Rules.Settings.RequireDescription), DescriptionCompletion.Attach(owner, description, () => projectId));
        descriptionArea.Children.Add(descriptionField);
        descriptionArea.Children.Add(reachSlot);
        if (proposed is not null)
        {
            if (proposed.Shares is { Length: > 1 }) ShowSplit(ProposalIssues.Parts(proposed));
        }
        else if (entry?.IsSplit() == true) ShowSplit(entry.Parts());
        if (begin?.Split is { Length: > 1 } parts) ShowSplit(parts);
        if (begin?.Stretches is { Length: > 1 } separate) stretches = separate;
        Content = Layout();
        // Empty slots take no room in the form.
        foreach (var slot in new[] { banner, suggestionSlot, splitSlot })
        {
            slot.IsVisible = slot.Content is not null;
            slot.PropertyChanged += (_, e) =>
            {
                if (e.Property == ContentProperty) slot.IsVisible = slot.Content is not null;
            };
        }
        ShowDuration();
        RenderFields();
        if (stopping && entry is not null) ShowMissing();
        ticking.Tick += (_, _) =>
        {
            if (endValue is null)
            {
                ShowDuration();
                split?.Tick();
            }
        };
        AttachedToVisualTree += async (_, _) =>
        {
            ticking.Start();
            // The first part still to be described, when the entry's own description isn't shown.
            if (split?.DescribesEach == true) split.FocusDescription(FirstToDescribe(split.Parts));
            else description.Focus();
            if (begin?.StartSplit == true && split is null) Dispatcher.UIThread.Post(() => BeginSplitPicker(projectSlot));
            await owner.Splits.LoadAsync([time.Day(startValue), time.Day(endValue ?? DateTimeOffset.UtcNow)]);
            RenderSuggestion();
        };
        DetachedFromVisualTree += (_, _) => ticking.Stop();
        KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                await SaveAsync(stopping);
                e.Handled = true;
            }
        };
    }

    public bool Saved { get; private set; }
    /// <summary>The entry saved, when it was saved as one entry, e.g. for an agent's proposal to name the entry it became.</summary>
    public Guid? SavedEntryId { get; private set; }

    private static DateTimeOffset Floor(DateTimeOffset value) => new(value.Ticks - value.Ticks % TimeSpan.TicksPerMinute, value.Offset);

    private string Title
    {
        get
        {
            if (proposed is not null) return entry is null ? "Proposed entry" : "Proposed change";
            if (entry is null) return "New time entry";
            if (locked) return "Time entry";
            if (stopping) return "Stop timer";
            return entry.IsRunning() ? "Edit running entry" : "Edit time entry";
        }
    }

    private string Label(string text, bool required) => required && !locked ? text + " (required)" : text;

    private Control Layout()
    {
        var header = Ui.Stack(4, Ui.Text(Title, "subtitle").Named("EditorTitle"),
            stopping ? Ui.Wrapped("Add the details this workspace requires, then stop the timer.", "secondary") : null);
        if (locked) banner.Content = Banner(owner.Rules.LockMessage(owner.Time), false);
        var times = Ui.Row(10,
            new FormField("Date", day),
            new FormField("Start", start),
            new FormField("End", end is not null ? end : new TextBox { Text = "Running", IsEnabled = false, Width = 84 }),
            new FormField("Duration", duration));
        var fields = Ui.Stack(16, banner, descriptionArea, suggestionSlot, splitSlot, pickers, billable, times, timeError);
        fields.IsEnabled = !locked;
        var body = Ui.Stack(18, header, fields);
        body.Margin = new Thickness(24, 24, 24, 20);
        var footer = new Border { Child = Buttons(), Padding = new Thickness(24, 16) }.Res(Border.BackgroundProperty, "CardBackgroundSecondaryBrush");
        return new DockPanel { Children = { footer.Dock(Dock.Bottom), new ScrollViewer { Content = body, MaxHeight = 640 } } };
    }

    private Control Buttons()
    {
        if (locked)
        {
            var done = Ui.Button("Done", Close, "primary");
            done.HorizontalAlignment = HorizontalAlignment.Right;
            return done;
        }
        var right = Ui.Row(8);
        right.HorizontalAlignment = HorizontalAlignment.Right;
        if (entry?.IsRunning() == true && !stopping) right.Children.Add(Ui.Button("Stop timer", () => SaveAsync(true), Fail).Named("EditorStop"));
        right.Children.Add(Ui.Button(stopping ? "Keep running" : "Cancel", Close).Named("EditorCancel"));
        var save = Ui.Button(entry is null ? "Add entry" : stopping ? "Save and stop" : "Save", () => SaveAsync(stopping), Fail, "primary").Named("EditorSave");
        save.MinWidth = 100;
        right.Children.Add(save);
        Control? delete = entry is null ? null : Ui.Button(Ui.Labeled("IconDelete", "Delete"), DeleteAsync, Fail, "danger").Named("EditorDelete");
        return Ui.Columns("Auto,*", 8, delete, right);
    }

    private void Close()
    {
        owner.Dialogs.Close(Saved);
    }

    private void Fail(string message) => banner.Content = Banner(message, true);

    private static Control Banner(string text, bool error)
    {
        var icon = error ? Ui.Icon("IconWarningFilled", "critical") : Ui.Icon("IconInfoFilled", "secondary");
        return new Border { Child = Ui.Columns("Auto,*", 10, icon.Centered(), Ui.Wrapped(text)) }.Classed("infobar", error ? "critical" : "").Named("EditorBanner");
    }

    // Project, tags, split

    private void RenderFields()
    {
        var catalog = owner.Catalog;
        billable.IsVisible = split is null && stretches is null;
        var perPart = split?.DescribesEach == true;
        descriptionArea.IsVisible = !perPart;
        RenderReach();
        if (split is not null && splitField is not null)
            splitField.Children.OfType<TextBlock>().First(t => t.Name == "Label").Text = perPart
                ? Label("Split and described per project", owner.Rules.Settings.RequireProject || owner.Rules.Settings.RequireDescription)
                : Label("Split between projects", owner.Rules.Settings.RequireProject);
        splitSlot.Content = stretches is not null ? new FormField("Separate entries", StretchList()) : split is not null ? splitField : null;
        if (split is null && stretches is null)
        {
            var taskRequired = owner.Rules.RequiresTask(catalog.Project(projectId));
            var button = PickerButton(SplitViews.ProjectOrNone(catalog, projectId, taskId)).Named("EditorProject");
            button.Click += (_, _) => PickerList.Show(button, ProjectPicker.Create(owner, projectId, taskId, (project, task) =>
            {
                projectId = project;
                taskId = task;
                billable.IsChecked = catalog.Project(project)?.IsBillable ?? billable.IsChecked;
                RenderFields();
                RenderSuggestion();
                return Task.CompletedTask;
            }, onSplit: () =>
            {
                BeginSplitPicker(button);
                return Task.CompletedTask;
            }));
            var splitLink = Ui.Button(Ui.Row(4, Ui.Icon("IconArrowSplit", "small").Centered(), Ui.Text("Split", "caption")), () => BeginSplitPicker(button), "link")
                .Tip("Split this time between projects").Named("EditorSplit");
            splitLink.IsVisible = !locked;
            var label = Ui.Columns("*,Auto", 8, Ui.Text(Label(taskRequired ? "Project and task" : "Project", owner.Rules.Settings.RequireProject || taskRequired), "medium"), splitLink);
            projectField = new FormField("", button);
            projectField.Children[0] = label;
            projectSlot.Content = projectField;
        }
        else
        {
            projectSlot.Content = null;
            projectField = null;
        }
        // The project and tags side by side; the tags alone, full width, while the time is split.
        pickers.Children.Clear();
        pickers.ColumnDefinitions = new ColumnDefinitions(projectSlot.Content is null ? "*" : "*,*");
        if (projectSlot.Content is not null) pickers.Children.Add(projectSlot);
        pickers.Children.Add(tagSlot.Column(projectSlot.Content is null ? 0 : 1));
        Control tags = tagIds.Length == 0
            ? Ui.Text("No tags", "secondary")
            : new WrapPanel { Children = { } };
        if (tags is WrapPanel wrap)
            foreach (var id in tagIds) wrap.Children.Add(Ui.Chip(catalog.Tag(id)?.Name ?? "…").Margin(0, 0, 4, 0));
        var tagButton = PickerButton(tags).Named("EditorTags").Spoken("Choose tags");
        tagButton.Click += (_, _) => PickerList.Show(tagButton, TagPicker.Create(owner, tagIds, chosen =>
        {
            tagIds = chosen;
            RenderFields();
            return Task.CompletedTask;
        }));
        tagField = new FormField(Label("Tags", owner.Rules.Settings.RequireTag), tagButton);
        tagSlot.Content = tagField;
    }

    /// <summary>A button that shows a choice and opens its picker, like a combo box.</summary>
    private static Button PickerButton(Control content)
    {
        var button = new Button
        {
            Content = Ui.Columns("*,Auto", 6, content, Ui.Icon("IconChevronDown", "secondary", "small").Centered()),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(11, 5, 11, 6),
        };
        return button;
    }

    /// <summary>Begins a split: what was chosen keeps half the time, the project picked gets the other half.</summary>
    private void BeginSplitPicker(Control anchor)
    {
        PickerList.Show(anchor, ProjectPicker.Create(owner, null, null, (project, task) =>
        {
            var keep = billable.IsChecked == true;
            ShowSplit([new SplitPart(projectId, taskId, keep, 50), new SplitPart(project, task, owner.Catalog.Project(project)?.IsBillable ?? false, 50)]);
            RenderFields();
            RenderSuggestion();
            return Task.CompletedTask;
        }, (p, t) => p == projectId && t == taskId, choosingAnew: true, placeholder: "Split the time with which project?"));
    }

    private void ShowSplit(IReadOnlyList<SplitPart> parts)
    {
        split = new SplitEditor(owner, parts, Seconds, endValue is null);
        // The editor keeps its place in the form while the label above it changes.
        splitField = new FormField("", split);
        // A split left with one part is that part's project again, described as that part was.
        split.Ended += only =>
        {
            projectId = only.ProjectId;
            taskId = only.TaskId;
            billable.IsChecked = only.IsBillable;
            split = null;
            if (only.Description is { } own) description.Text = own;
            RenderFields();
            RenderSuggestion();
            if (only.Description is not null) Dispatcher.UIThread.Post(() => description.Focus());
        };
        split.DescribedAsOne += DescribeAllAsOne;
        split.Rendered += RenderReach;
    }

    /// <summary>
    /// Under the description of a split: every project shows it, timesheet links included, with a way to describe
    /// each on its own.
    /// </summary>
    private void RenderReach()
    {
        if (split is null || split.DescribesEach || locked)
        {
            reachSlot.Content = null;
            reachSlot.IsVisible = false;
            return;
        }
        var describe = Ui.Button("Describe per project", DescribePerPart, "link").Named("EditorDescribePerProject")
            .Tip("Give each project its own description; its reports and timesheet links show only that");
        reachSlot.Content = Ui.Row(6, Ui.Icon("IconEye", "secondary", "small").Centered(),
            Ui.Text($"Shown on all {split.Parts.Count} projects, timesheet links included", "caption", "secondary").Centered(),
            Ui.Text("·", "caption", "tertiary").Centered(), describe.Centered()).Named("EditorDescriptionReach");
        reachSlot.IsVisible = true;
    }

    /// <summary>Each project gets its own description, and the first one still to be written is ready for typing.</summary>
    private void DescribePerPart()
    {
        if (split is null) return;
        var described = SplitShares.DescribingEach(split.Parts, description.Text ?? "");
        split.Parts.Clear();
        split.Parts.AddRange(described);
        descriptionField.Error = null;
        split.Render();
        RenderFields();
        split.FocusDescription(FirstToDescribe(described));
    }

    /// <summary>One description for all parts again: theirs joined.</summary>
    private void DescribeAllAsOne()
    {
        if (split is null) return;
        var (parts, joined) = SplitShares.SharingOneDescription(split.Parts);
        split.Parts.Clear();
        split.Parts.AddRange(parts);
        split.PartErrors.Clear();
        description.Text = joined;
        split.Render();
        RenderFields();
        Dispatcher.UIThread.Post(() =>
        {
            description.Focus();
            description.CaretIndex = joined.Length;
        });
    }

    private static int FirstToDescribe(IReadOnlyList<SplitPart> parts) =>
        Enumerable.Range(0, parts.Count).FirstOrDefault(i => string.IsNullOrEmpty(parts[i].Description));

    private double Seconds() => Math.Max(0, ((endValue ?? DateTimeOffset.UtcNow) - startValue).TotalSeconds);

    // Agent activity

    /// <summary>What agent activity suggests for this time, unless the split already shows it.</summary>
    private void RenderSuggestion()
    {
        suggestionSlot.Content = null;
        if (locked || stretches is not null || entry is not null && owner.Splits.IsDismissed(entry.Id)) return;
        var tracked = split?.Parts.MaxBy(p => p.Percent)?.ProjectId ?? projectId;
        if (owner.Splits.Suggestion(startValue, endValue ?? DateTimeOffset.UtcNow, tracked) is not { } suggestion) return;
        var current = split?.Parts.ToArray() ?? [new SplitPart(projectId, taskId, billable.IsChecked == true, 100)];
        var shown = current.GroupBy(p => p.ProjectId).ToDictionary(g => g.Key ?? Guid.Empty, g => g.Sum(p => p.Percent));
        var matches = shown.Count == suggestion.Parts.Count && suggestion.Parts.All(p => p.Target.ProjectId is { } id && shown.TryGetValue(id, out var percent) && Math.Abs(percent - p.Percent) <= 5);
        if (matches) return;
        suggestionSlot.Content = new SuggestionBanner(owner, suggestion, take =>
        {
            if (take.Stretches is { Length: > 1 } separate && entry?.IsRunning() != true)
            {
                stretches = separate;
                split = null;
            }
            else if (take.Parts is { } parts)
            {
                ShowSplit(parts);
            }
            RenderFields();
            RenderSuggestion();
        }, entry is null ? null : async () =>
        {
            await owner.Splits.DismissAsync(entry);
            RenderSuggestion();
        }, separable: entry?.IsRunning() != true, keepTask: taskId, keepBillable: billable.IsChecked == true, tracked: projectId, refresh: RenderSuggestion);
    }

    /// <summary>The entries a stretch of time will become, one after another, each on its project.</summary>
    private Control StretchList()
    {
        var list = Ui.Stack(6);
        var catalog = owner.Catalog;
        for (var index = 0; index < stretches!.Length; index++)
        {
            var from = index == 0 ? startValue : stretches[index].Start;
            DateTimeOffset? to = index == stretches.Length - 1 ? endValue : stretches[index + 1].Start;
            var project = catalog.Project(stretches[index].ProjectId);
            list.Children.Add(Ui.Columns("Auto,*,Auto,Auto", 8, Ui.Dot(project?.Color, 8), Ui.Text(project?.Name ?? "Project", "medium"),
                Ui.Text(owner.Time.Span(from, to), "secondary", "mono"), Ui.Text(Durations.Clock(((to ?? DateTimeOffset.UtcNow) - from).TotalSeconds), "secondary", "mono").Margin(8, 0, 0, 0)));
        }
        var keep = Ui.Button("Back to one entry", () =>
        {
            stretches = null;
            RenderFields();
            RenderSuggestion();
        }, "link");
        list.Children.Add(Ui.Columns("*,Auto", 8, Ui.Text("Each entry keeps the description and tags.", "caption", "secondary").Centered(), keep));
        return Ui.Card(list);
    }

    // Times

    private void ShowDuration()
    {
        if (!duration.IsFocused) duration.Text = Durations.Clock(Seconds());
    }

    private DateTimeOffset? Read(ClockField field, DateOnly on, DateTimeOffset original, out string? problem)
    {
        problem = null;
        if (field.Text == owner.Time.ClockField(original) && owner.Time.Day(original) == on) return original;
        if (field.Clock is not { } clock)
        {
            problem = UnreadableTime;
            return null;
        }
        var local = on.ToDateTime(new TimeOnly(clock.Hours, clock.Minutes));
        if (owner.Zone.IsInvalidTime(local))
        {
            problem = "This time does not exist because the clocks move forward. Choose another time.";
            return null;
        }
        if (owner.Zone.IsAmbiguousTime(local))
        {
            problem = "This time occurs twice because the clocks move back. Choose an unambiguous time.";
            return null;
        }
        return owner.Time.At(on, clock);
    }

    /// <summary>A new start keeps the end where it is, so the duration changes.</summary>
    private bool CommitStart()
    {
        var next = Read(start, day.Day, startValue, out var problem);
        timeError.Text = problem;
        timeError.IsVisible = problem is not null;
        if (next is null) return false;
        startValue = next.Value;
        ShowDuration();
        RenderSuggestion();
        return true;
    }

    /// <summary>An end before the start means the entry ran past midnight.</summary>
    private bool CommitEnd()
    {
        if (end is null || endValue is not { } previous) return true;
        var next = Read(end, owner.Time.Day(startValue), previous, out var problem);
        if (next is { } value && value < startValue && value != previous) next = Read(end, owner.Time.Day(startValue).AddDays(1), previous, out problem);
        if (problem is not null)
        {
            timeError.Text = problem;
            timeError.IsVisible = true;
            return false;
        }
        endValue = next;
        ShowDuration();
        RenderSuggestion();
        return true;
    }

    private void CommitDuration()
    {
        if (endValue is not null && Durations.Parse(duration.Text ?? "") is { } seconds && Math.Abs(seconds - Seconds()) >= 1)
        {
            endValue = startValue.AddSeconds(seconds);
            end!.Text = owner.Time.ClockField(endValue.Value);
        }
        ShowDuration();
    }

    /// <summary>Moving to another day keeps the clock times, counted in calendar days.</summary>
    private void ChangeDay(DateOnly newDay)
    {
        var time = owner.Time;
        var offset = newDay.DayNumber - time.Day(startValue).DayNumber;
        startValue = time.At(newDay, time.ClockOf(startValue));
        if (endValue is { } stop) endValue = time.At(time.Day(stop).AddDays(offset), time.ClockOf(stop));
        start.Text = time.ClockField(startValue);
        if (end is not null && endValue is { } value) end.Text = time.ClockField(value);
        ShowDuration();
    }

    // Saving

    private void ShowMissing()
    {
        var parts = split?.Parts.ToArray() ?? [];
        var missing = owner.Rules.Missing(description.Text ?? "", projectId, taskId, tagIds, parts.Length > 1, owner.Catalog.Project(projectId), SplitShares.DescribesEach(parts));
        descriptionField.Error = missing.FirstOrDefault(m => m.Field == "description").Message;
        if (projectField is not null) projectField.Error = missing.FirstOrDefault(m => m.Field is "projectId" or "taskId").Message;
        if (tagField is not null) tagField.Error = missing.FirstOrDefault(m => m.Field == "tagIds").Message;
        if (split is not null)
        {
            split.PartErrors.Clear();
            foreach (var (index, message) in owner.Rules.MissingInParts(parts, owner.Catalog)) split.PartErrors[index] = message;
            split.Render();
        }
    }

    private bool HasMissing()
    {
        var parts = split?.Parts.ToArray() ?? [];
        return owner.Rules.Missing(description.Text ?? "", projectId, taskId, tagIds, parts.Length > 1, owner.Catalog.Project(projectId), SplitShares.DescribesEach(parts)).Count > 0
            || parts.Length > 1 && owner.Rules.MissingInParts(parts, owner.Catalog).Count > 0;
    }

    /// <summary>Saves the entry; <paramref name="stopNow"/> also stops a running timer, keeping the changes made here.</summary>
    public async Task SaveAsync(bool stopNow)
    {
        if (saving) return;
        if (!CommitStart() || !CommitEnd()) return;
        CommitDuration();
        var stop = stopNow ? DateTimeOffset.UtcNow : endValue;
        if (stop is { } finish && finish < startValue)
        {
            timeError.Text = "The end must not be before the start.";
            timeError.IsVisible = true;
            return;
        }
        if (startValue > DateTimeOffset.UtcNow.AddMinutes(1) && entry?.Start != startValue)
        {
            timeError.Text = "The start must not be in the future.";
            timeError.IsVisible = true;
            return;
        }
        // A running entry may lack details for now; a finished one must have what the workspace requires.
        if (stop is not null && HasMissing())
        {
            ShowMissing();
            return;
        }
        descriptionField.Error = null;
        var parts = split?.Parts.ToArray() ?? [];
        if (parts.Length > 1 && (parts.Sum(p => p.Percent) != 100 || parts.Any(p => p.Percent < 1)))
        {
            split!.Error = "Use 2–6 parts totaling 100%.";
            return;
        }
        saving = true;
        try
        {
            if (stretches is { Length: > 1 } separate) await SaveSeparatelyAsync(separate, stop);
            else await SaveOneAsync(parts, stop);
            if (stopNow) owner.Notify("Timer stopped", Shell.NoticeKind.Success);
            Saved = true;
            Close();
        }
        catch (ApiException exception)
        {
            Fail(exception.Message);
        }
        catch (Exception exception) when (exception is InvalidOperationException or HttpRequestException)
        {
            Fail(exception.Message);
        }
        finally
        {
            saving = false;
        }
    }

    private async Task SaveOneAsync(SplitPart[] parts, DateTimeOffset? stop)
    {
        // The largest part is the entry's project, for apps that show one; the server keeps the first of equal ones.
        var main = parts.Length > 1 ? parts.First(p => p.Percent == parts.Max(q => q.Percent)) : null;
        var perPart = SplitShares.DescribesEach(parts);
        var request = new TimeEntryRequest
        {
            // Parts described one by one make the entry's description of theirs, as the server does.
            Description = perPart ? SplitShares.JoinedDescriptions(parts) : (description.Text ?? "").Trim(),
            ProjectId = main?.ProjectId ?? projectId,
            TaskId = main is null ? taskId : main.TaskId,
            TagIds = tagIds,
            IsBillable = main?.IsBillable ?? billable.IsChecked == true,
            Start = startValue,
            Stop = stop,
            Shares = parts.Length > 1 ? parts.Select(p => p.Request()).ToArray() : [],
            // A split says whether its parts are described one by one, so going back to one description sticks.
            DescribeParts = parts.Length > 1 ? perPart : null,
        };
        if (entry is null)
        {
            var id = Guid.NewGuid();
            await owner.MutateAsync(() => owner.CreateEntryAsync(new CreateTimeEntryRequest
            {
                Id = id,
                Description = request.Description,
                ProjectId = request.ProjectId,
                TaskId = request.TaskId,
                TagIds = request.TagIds,
                IsBillable = request.IsBillable,
                Start = request.Start,
                Stop = request.Stop,
                Shares = parts.Length > 1 ? request.Shares : null,
                DescribeParts = request.DescribeParts,
            }));
            SavedEntryId = id;
        }
        else
        {
            await owner.MutateAsync(() => owner.UpdateEntryAsync(entry, request));
            SavedEntryId = entry.Id;
        }
    }

    /// <summary>
    /// Saves the time as consecutive entries, cut where the stretches meet: this entry becomes the last stretch, so a
    /// running timer keeps running on it, and the earlier stretches are added before it.
    /// </summary>
    private async Task SaveSeparatelyAsync((Guid ProjectId, DateTimeOffset Start)[] separate, DateTimeOffset? stop)
    {
        var cuts = separate.Skip(1).Select(s => s.Start).ToArray();
        if (cuts.Any(c => c <= startValue || stop is { } finish && c >= finish) || cuts.Zip(cuts.Skip(1)).Any(p => p.First >= p.Second))
            throw new InvalidOperationException("The start or end moved past where the entries meet. Go back to one entry and split it again.");
        var text = (description.Text ?? "").Trim();
        bool Billable(Guid id) => owner.Catalog.Project(id)?.IsBillable ?? false;
        Guid? TaskOn(Guid id) => id == projectId ? taskId : null;
        await owner.MutateAsync(async () =>
        {
            for (var index = 0; index < separate.Length - 1; index++)
            {
                var piece = separate[index];
                await owner.CreateEntryAsync(new CreateTimeEntryRequest
                {
                    Id = Guid.NewGuid(),
                    Description = text,
                    ProjectId = piece.ProjectId,
                    TaskId = TaskOn(piece.ProjectId),
                    TagIds = tagIds,
                    IsBillable = Billable(piece.ProjectId),
                    Start = index == 0 ? startValue : piece.Start,
                    Stop = separate[index + 1].Start,
                });
            }
            var last = separate[^1];
            var request = new TimeEntryRequest { Description = text, ProjectId = last.ProjectId, TaskId = TaskOn(last.ProjectId), TagIds = tagIds, IsBillable = Billable(last.ProjectId), Start = last.Start, Stop = stop, Shares = [] };
            if (entry is null)
                await owner.CreateEntryAsync(new CreateTimeEntryRequest { Id = Guid.NewGuid(), Description = text, ProjectId = request.ProjectId, TaskId = request.TaskId, TagIds = tagIds, IsBillable = request.IsBillable, Start = request.Start, Stop = stop });
            else
                await owner.UpdateEntryAsync(entry, request);
        });
        owner.Notify($"Split into {separate.Length} entries", Shell.NoticeKind.Success);
    }

    private async Task DeleteAsync()
    {
        if (entry is null) return;
        var what = entry.Description.Length == 0 ? "The entry" : $"“{entry.Description}”";
        owner.Dialogs.Close(false);
        await Task.Yield();
        if (!await owner.ConfirmAsync("Delete this time entry?", $"{what} and its {Durations.Clock(Seconds())} will be removed.", "Delete", destructive: true)) return;
        await owner.MutateAsync(() => owner.DeleteEntryAsync(entry));
        owner.Notify("Time entry deleted", Shell.NoticeKind.Success);
    }
}

/// <summary>
/// "Agents worked on 2 projects in this time": the split agent activity suggests, to apply with one click, or as
/// separate entries when the work moved from one project to the next. A repository without a project is linked here.
/// </summary>
public sealed class SuggestionBanner : UserControl
{
    public SuggestionBanner(MainWindow owner, SplitSuggestion suggestion, Action<SplitTake> onTake, Func<Task>? onDismiss, bool separable, Guid? keepTask, bool keepBillable, Guid? tracked, Action refresh)
    {
        var take = owner.Splits.Take(suggestion, keepTask, keepBillable, tracked);
        var unlinked = suggestion.Parts.Any(p => p.Target.ProjectId is null);
        var title = unlinked ? $"Agents worked in {suggestion.Parts.Count} places in this time" : $"Agents worked on {suggestion.Parts.Count} projects in this time";
        var buttons = Ui.Row(6);
        if (onDismiss is not null) buttons.Children.Add(Ui.Button("Keep as is", onDismiss, owner.Notify, "subtle").Tip("Leave the entry as it is and stop suggesting a split for it"));
        var canSeparate = separable && suggestion.Stretches is not null;
        if (canSeparate)
        {
            var shares = Ui.Button("Split " + string.Join(" · ", suggestion.Parts.Select(p => p.Percent)), () => onTake(take! with { Stretches = null }));
            shares.IsEnabled = take is not null;
            ToolTip.SetTip(shares, take is null ? "Choose a project for each repository first." : "Keep one entry and share its time by percent; nothing is saved until you save.");
            var separate = Ui.Button(owner.Splits.Title(suggestion), () => onTake(take!), "primary").Named("ApplySuggestion");
            separate.IsEnabled = take?.Stretches is not null;
            ToolTip.SetTip(separate, "Make separate entries, cut where the work moved; nothing is saved until you save.");
            buttons.Children.Add(shares);
            buttons.Children.Add(separate);
        }
        else
        {
            var apply = Ui.Button("Split", () => onTake(take! with { Stretches = null }), "primary").Named("ApplySuggestion");
            apply.IsEnabled = take is not null;
            ToolTip.SetTip(apply, take is null ? "Choose a project for each repository first." : "Split the time as suggested; nothing is saved until you save.");
            buttons.Children.Add(apply);
        }
        var header = Ui.Columns("Auto,*,Auto", 8, Ui.Icon("IconSparkle", "brand").Centered(), Ui.Text(title, "strong").Centered(), buttons);
        var parts = new WrapPanel();
        foreach (var part in suggestion.Parts)
        {
            var name = part.Target.ProjectId is { } id ? owner.Catalog.Project(id)?.Name ?? "Project" : part.Target.Repository?.Name ?? "Repository";
            var prompts = suggestion.Touches.GetValueOrDefault(part.Target);
            var sent = $"{prompts} {(prompts == 1 ? "prompt" : "prompts")}";
            var label = Ui.Row(5, Ui.Dot(SplitChips.ColorOf(owner, part.Target), 8), Ui.Text(name, "medium"), Ui.Text($"{part.Percent}%", "secondary", "mono"),
                Ui.Text("· " + Durations.HoursMinutes((part.Attention + part.Unattributed) / 1000d), "tertiary", "mono").Tip(part.Unattributed > 0
                    ? $"{Durations.HoursMinutes(part.Attention / 1000d)} in agent sessions there ({sent}), and {Durations.HoursMinutes(part.Unattributed / 1000d)} the logs don't account for (hatched)"
                    : $"Time in agent sessions there, {sent}"));
            if (part.Target.Repository is { } repository && part.Target.ProjectId is null)
            {
                var link = Ui.Button(Ui.Row(4, Ui.Icon("IconLink", "small").Centered(), Ui.Text("Choose project", "caption")), () => { }, "link")
                    .Tip($"Which project is {repository.Name} for? It is remembered for your account, on the web too.");
                link.Click += (_, _) => PickerList.Show(link, ProjectPicker.Create(owner, null, null, async (project, _) =>
                {
                    if (project is { } chosen) await owner.Splits.LinkAsync(repository, chosen);
                    else await RepositoryLinkButton.UnlinkAsync(owner, repository);
                    refresh();
                }, choosingAnew: true, placeholder: $"Which project is {repository.Name} for?",
                onIgnore: async () =>
                {
                    await RepositoryLinkButton.IgnoreAsync(owner, repository);
                    refresh();
                }));
                label.Children.Add(link);
            }
            else if (part.Repositories.FirstOrDefault() is { } linked)
            {
                // A repository whose branches belong to different projects, e.g. a monorepo.
                var branches = Ui.IconButton("IconBranch", $"Different projects by branch in {linked.Name}", async () =>
                {
                    await BranchRulesEditor.ShowAsync(owner, linked);
                    refresh();
                }, owner.Notify, "subtle").Tip($"Different projects by branch in {linked.Name}…").Named("BannerBranchRules");
                label.Children.Add(branches.Centered());
            }
            parts.Children.Add(label.Margin(0, 0, 14, 4));
        }
        // Where the work moved; the strip shows how much of the time the logs account for.
        string? note = null;
        if (canSeparate && suggestion.Stretches is { } stretches)
        {
            var moves = stretches.Skip(1).Select(s => owner.Time.Clock(DateTimeOffset.FromUnixTimeMilliseconds(s.Start))).ToArray();
            note = moves.Length == 1 ? $"The work moved to {EvidenceStrip.NameOf(owner, stretches[1].Target)} at {moves[0]}." : $"The work moved at {string.Join(", ", moves[..^1])} and {moves[^1]}.";
        }
        var content = Ui.Stack(8, header, EvidenceStrip.WithTimes(owner, suggestion), parts,
            note is not null ? Ui.Wrapped(note, "caption", "secondary") : null);
        Content = new Border { Child = content, Padding = new Thickness(12) }.Classed("card").Named("SuggestionBanner");
    }
}
