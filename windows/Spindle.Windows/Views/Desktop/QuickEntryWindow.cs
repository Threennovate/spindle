using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Spindle.Core;
using Spindle.Windows.Model;
using Spindle.Windows.Views.Shell;
using static Spindle.Windows.Model.EntrySentence;

namespace Spindle.Windows.Views;

/// <summary>A row of quick entry: what Enter does when it is selected.</summary>
public sealed record QuickRow(string Id, string Title, string? Detail, string Primary, Func<Task> Run, string? Section = null, Control? Leading = null, string? Meta = null, bool KeepOpen = false, bool Blocked = false);

/// <summary>
/// The panel the quick entry shortcut opens over any app, as the Mac's: type what you work on, pick a project with @
/// and tags with #, add $ for billable and a time at the end ("since 9:40", "9:00-10:30", "yesterday for 2h"), then
/// press Enter. With nothing typed it stops or starts the timer, or continues a recent entry.
/// </summary>
public sealed class QuickEntryWindow : Window
{
    private readonly MainWindow owner;
    private readonly TextBox input = new TextBox { Name = "QuickEntryText" }.Classed("bare");
    private readonly StackPanel chipStrip = new() { Orientation = Orientation.Horizontal, Spacing = 4 };
    private readonly StackPanel list = new() { Spacing = 1 };
    private readonly ScrollViewer scroller;
    private readonly ContentControl runningCard = new();
    private readonly TextBlock footerLeft = Ui.Text("", "caption", "secondary");
    private readonly StackPanel footerRight = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
    private readonly List<Chip> chips = [];
    private IReadOnlyList<QuickRow> rows = [];
    private int selected;
    private string? error;

    public QuickEntryWindow(MainWindow owner)
    {
        this.owner = owner;
        Width = 640;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        Topmost = true;
        WindowDecorations = WindowDecorations.None;
        Title = "Spindle quick entry";
        this.Res(BackgroundProperty, "FlyoutBackgroundBrush");
        input.FontSize = 17;
        input.VerticalAlignment = VerticalAlignment.Center;
        Avalonia.Automation.AutomationProperties.SetName(input, "Quick entry");
        Avalonia.Automation.AutomationProperties.SetHelpText(input, "Type @ for a project, # for a tag, $ for billable, and a time at the end, such as since 9:40 or 9:00-10:30.");
        input.TextChanged += (_, _) =>
        {
            selected = 0;
            error = null;
            Render();
        };
        input.AddHandler(KeyDownEvent, OnKey, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        var header = Ui.Columns("Auto,Auto,*,Auto", 10, Ui.Icon("IconSearch", "secondary", "large").Centered(), chipStrip.Centered(), input, Ui.Keycap("Esc"));
        header.Height = 54;
        header.Margin = new Thickness(16, 0);
        scroller = new ScrollViewer { Content = list, MaxHeight = 420, Padding = new Thickness(6) };
        var footer = Ui.Columns("*,Auto", 12, footerLeft.Centered(), footerRight.Centered());
        footer.Height = 34;
        footer.Margin = new Thickness(16, 0);
        var stack = Ui.Stack(0, header, Ui.Divider(), runningCard, scroller, Ui.Divider(), footer);
        Content = new Border { Child = stack, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8) }.Res(Border.BorderBrushProperty, "FlyoutStrokeBrush");
        Deactivated += (_, _) => Hide();
        Opened += (_, _) => Place();
    }

    /// <summary>Opens the panel fresh: no chips, nothing typed, the top row selected.</summary>
    public void Open()
    {
        chips.Clear();
        input.Text = "";
        selected = 0;
        error = null;
        Render();
        Show();
        Place();
        Activate();
        Dispatcher.UIThread.Post(() => input.Focus());
    }

    public void Toggle()
    {
        if (IsVisible) Hide();
        else Open();
    }

    private void Place()
    {
        if (Screens.Primary is not { } screen) return;
        var area = screen.WorkingArea;
        var scale = screen.Scaling;
        Position = new PixelPoint(area.X + (area.Width - (int)(Width * scale)) / 2, area.Y + area.Height / 5);
    }

    internal IReadOnlyList<QuickRow> Rows => rows;
    internal Task RunRowAsync(int index) => RunAsync(rows[index]);
    internal void Type(string text)
    {
        input.Text = text;
        input.CaretIndex = text.Length;
        Render();
    }

    // What the line means

    private Options SentenceOptions => new(owner.Time.Hour12, owner.Me?.Preferences.DateFormat is { Length: > 0 } format ? format : "dd.MM.yyyy");

    private Mention? CurrentMention => MentionAt(input.Text ?? "", input.CaretIndex);

    private void Render()
    {
        var text = input.Text ?? "";
        input.PlaceholderText = chips.Count > 0 ? "Describe it, add a time…"
            : owner.Running is not null ? "Search, or type what you’re doing next…"
            : "What are you working on? @project #tag since 9:40";
        RenderChips();
        rows = owner.Me is null ? [] : Compute(text);
        selected = Math.Clamp(selected, 0, Math.Max(0, rows.Count - 1));
        runningCard.Content = text.Length == 0 && chips.Count == 0 && owner.Running is { } running ? RunningCard(running) : null;
        list.Children.Clear();
        string? section = null;
        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            if (row.Section is { } title && title != section) list.Children.Add(Ui.Text(title.ToUpperInvariant(), "caption", "medium", "secondary").Margin(10, index == 0 ? 2 : 10, 0, 4));
            section = row.Section;
            list.Children.Add(RowView(row, index));
        }
        if (rows.Count == 0)
            list.Children.Add(Ui.Text(owner.Me is null ? "Sign in to Spindle to track time from here." : "Nothing fits.", "secondary").Margin(10, 10, 10, 10));
        footerLeft.Classes.Set("critical", error is not null);
        footerLeft.Text = error ?? owner.Workspace?.Name ?? "Spindle";
        footerRight.Children.Clear();
        if (rows.Count > 0)
        {
            footerRight.Children.Add(Ui.Text(rows[selected].Primary, "caption", "strong").Centered());
            footerRight.Children.Add(Ui.Keycap("Enter"));
            if (CurrentMention is not null && rows[selected].Id.StartsWith("mention:", StringComparison.Ordinal))
            {
                footerRight.Children.Add(Ui.Text("Pick", "caption", "secondary").Centered().Margin(8, 0, 0, 0));
                footerRight.Children.Add(Ui.Keycap("Tab"));
            }
        }
    }

    private void RenderChips()
    {
        chipStrip.Children.Clear();
        foreach (var chip in chips)
        {
            Control content = chip switch
            {
                Chip.Project p => Ui.Row(5, Ui.Dot(owner.Catalog.Project(Guid.Parse(p.ProjectId))?.Color, 8), Ui.Text(owner.Catalog.Task(p.TaskId is null ? null : Guid.Parse(p.TaskId))?.Name ?? owner.Catalog.Project(Guid.Parse(p.ProjectId))?.Name ?? "Project", "caption", "medium")),
                Chip.Client c => Ui.Row(5, Ui.Icon("IconBuildingMultiple", "small"), Ui.Text(owner.Catalog.Client(Guid.Parse(c.ClientId))?.Name ?? "Client", "caption", "medium")),
                Chip.Tag t => Ui.Text("#" + (owner.Catalog.Tag(Guid.Parse(t.TagId))?.Name ?? "tag"), "caption", "medium"),
                _ => Ui.Text("$ Billable", "caption", "medium", "brand"),
            };
            chipStrip.Children.Add(new Border { Child = content, Padding = new Thickness(8, 3) }.Classed("chip"));
        }
        chipStrip.IsVisible = chips.Count > 0;
    }

    private Control RowView(QuickRow row, int index)
    {
        var text = Ui.Stack(1, Ui.Text(row.Title), row.Detail is null ? null : Ui.Wrapped(row.Detail, "caption", row.Blocked ? "critical" : "secondary"));
        var leading = new Border { Width = 18, Child = row.Leading, VerticalAlignment = VerticalAlignment.Center };
        var meta = row.Meta is null ? null : Ui.Text(row.Meta, "caption", "secondary", "mono").Centered();
        var button = new Button { Content = Ui.Columns("Auto,*,Auto", 10, leading, text.Centered(), meta), Padding = new Thickness(10, 7), MinHeight = row.Detail is null ? 36 : 52, CornerRadius = new CornerRadius(6) }
            .Classed("row").Spoken(row.Title);
        if (index == selected) button.Classes.Add("highlighted");
        button.PointerEntered += (_, _) =>
        {
            if (selected == index) return;
            selected = index;
            foreach (var other in list.Children.OfType<Button>()) other.Classes.Remove("highlighted");
            button.Classes.Add("highlighted");
        };
        button.Click += async (_, _) =>
        {
            selected = index;
            await RunAsync(row);
        };
        return button;
    }

    private Control RunningCard(TimeEntryResponse running)
    {
        var catalog = owner.Catalog;
        Control project = running.IsSplit() ? SplitViews.Label(catalog, running.Parts()) : SplitViews.ProjectOrNone(catalog, running.ProjectId, running.TaskId);
        var clock = Ui.Text(Durations.Clock(running.Seconds(DateTimeOffset.UtcNow)), "clock");
        var text = Ui.Stack(2, Ui.Text(running.Description.Length == 0 ? "No description" : running.Description, "strong", running.Description.Length == 0 ? "secondary" : ""), project);
        var dot = new Avalonia.Controls.Shapes.Ellipse { Width = 8, Height = 8 }.Res(Avalonia.Controls.Shapes.Shape.FillProperty, "BrandBrush");
        return new Border { Child = Ui.Columns("Auto,*,Auto", 12, dot.Centered(), text, clock.Centered()), Padding = new Thickness(18, 10), BorderThickness = new Thickness(0, 0, 0, 1) }
            .Res(Border.BorderBrushProperty, "DividerBrush");
    }

    // Rows

    private IReadOnlyList<QuickRow> Compute(string text)
    {
        if (CurrentMention is { } mention) return MentionRows(mention, text);
        if (text.Trim().Length == 0 && chips.Count == 0) return IdleRows();
        var result = new List<QuickRow>();
        if (owner.Running is { } running && chips.Count == 0 && StartChange(text.Trim(), running) is { } change) result.Add(change);
        var reading = Read(text, chips, SentenceOptions);
        result.Add(Preview(reading));
        if (reading.Time is not null)
            result.Add(new QuickRow("sentence.as-written", $"Start “{text.Trim()}”", "as written, without reading a time", "Start timer",
                () => owner.StartAsync(new CreateTimeEntryRequest { Description = text.Trim() }), Leading: Ui.Icon("IconPlay", "secondary", "small")));
        if (chips.Count == 0)
        {
            var term = text.Trim();
            foreach (var entry in Recent(e => e.Description.Contains(term, StringComparison.CurrentCultureIgnoreCase)).Take(4))
                result.Add(ContinueRow(entry, "Earlier entries"));
        }
        return result;
    }

    private IEnumerable<TimeEntryResponse> Recent(Func<TimeEntryResponse, bool>? matches = null) => owner.Entries
        .Where(e => e.Stop is not null && (matches?.Invoke(e) ?? true))
        .OrderByDescending(e => e.Start)
        .DistinctBy(e => (e.Description, e.ProjectId, e.TaskId, string.Join(",", e.TagIds), string.Join(",", e.Shares.Select(s => $"{s.ProjectId}{s.Percent}"))));

    private IReadOnlyList<QuickRow> IdleRows()
    {
        var result = new List<QuickRow>();
        if (owner.Running is { } running)
        {
            var seconds = running.Seconds(DateTimeOffset.UtcNow);
            result.Add(new QuickRow("timer.stop", "Stop timer", $"Started {owner.Time.Clock(running.Start)}", "Stop", async () =>
            {
                await owner.StopAsync();
                Done($"Stopped at {Durations.Clock(seconds)}");
            }, Leading: Ui.Icon("IconStopFilled", "critical", "small")));
        }
        else
        {
            result.Add(new QuickRow("timer.start", "Start timer", null, "Start", async () =>
            {
                await owner.StartAsync(new CreateTimeEntryRequest());
                Done("Timer started");
            }, "Timer", Ui.Icon("IconPlayFilled", "brand", "small")));
        }
        foreach (var entry in Recent().Take(owner.Running is null ? 4 : 3)) result.Add(ContinueRow(entry, "Continue"));
        return result;
    }

    private QuickRow ContinueRow(TimeEntryResponse entry, string section)
    {
        var catalog = owner.Catalog;
        var detail = entry.IsSplit() ? catalog.Describe(entry.Parts()) : catalog.Project(entry.ProjectId) is { } project ? project.Name + (catalog.Task(entry.TaskId) is { } task ? " · " + task.Name : "") : null;
        var name = entry.Description.Length == 0 ? "No description" : entry.Description;
        Control leading = entry.IsSplit() ? SplitViews.Dots(catalog, entry.Parts(), 8) : Ui.Dot(catalog.Project(entry.ProjectId)?.Color, 8);
        return new QuickRow("continue:" + entry.Id, name, detail, owner.Running is null ? "Continue" : "Switch to this", async () =>
        {
            await owner.ContinueAsync(entry);
            Done($"Continuing “{name}”");
        }, section, leading);
    }

    /// <summary>"+15" or "-15" while a timer runs: started that much earlier or later.</summary>
    private QuickRow? StartChange(string text, TimeEntryResponse running)
    {
        if (text.Length < 2 || text[0] is not ('+' or '-') || Durations.Parse(text[1..]) is not { } seconds) return null;
        var earlier = text[0] == '+';
        var start = running.Start.AddSeconds(earlier ? -seconds : seconds);
        var now = DateTimeOffset.UtcNow;
        var amount = seconds >= 3600 ? Durations.ShortClock(seconds) : $"{seconds / 60} min";
        var problem = start > now ? "That is after now." : owner.Rules.IsLocked(owner.Time.Day(start)) ? owner.Rules.LockMessage(owner.Time) : null;
        return new QuickRow("timer.start-change", $"Started {amount} {(earlier ? "earlier" : "later")}",
            problem ?? $"{Durations.Clock(running.Seconds(now))} → {Durations.Clock((now - start).TotalSeconds)}",
            problem is null ? "Change start" : "Can’t change",
            async () =>
            {
                if (problem is not null) throw new InvalidOperationException(problem);
                await owner.UpdateRunningAsync(r => r with { Start = start });
                Done($"Started at {owner.Time.Clock(start)}");
            }, Leading: Ui.Icon("IconClock", "secondary", "small"), Blocked: problem is not null);
    }

    /// <summary>The line as an entry: "Start “Review PR”" or "Add “Standup”", with what it will have.</summary>
    private QuickRow Preview(Reading reading)
    {
        var now = DateTimeOffset.UtcNow;
        var time = owner.Time;
        var span = Resolve(reading.Time, now, time, day => owner.Entries.Where(e => e.Stop is { } stop && time.Day(stop) == day).Select(e => e.Stop!.Value));
        var adding = span.Stop is not null;
        var verb = adding ? "Add" : "Start";
        var description = reading.Description;
        var title = description.Length > 0 ? $"{verb} “{description}”" : adding ? "Add time" : "Start timer";
        Guid? projectId = reading.ProjectId is null ? null : Guid.Parse(reading.ProjectId);
        Guid? taskId = reading.TaskId is null ? null : Guid.Parse(reading.TaskId);
        var tags = reading.TagIds.Select(Guid.Parse).ToArray();
        var project = owner.Catalog.Project(projectId);
        var facts = new List<string>();
        if (project is not null) facts.Add(project.Name + (owner.Catalog.Task(taskId) is { } task ? " · " + task.Name : ""));
        facts.AddRange(tags.Select(t => "#" + (owner.Catalog.Tag(t)?.Name ?? "tag")));
        if (reading.Billable) facts.Add("Billable");
        var day = time.Day(span.Start);
        var dayText = day == time.Today() ? "" : " " + time.DayHeading(day).ToLowerInvariant();
        if (span.Stop is { } stop) facts.Add($"{(day == time.Today() ? "" : time.DayHeading(day) + " ")}{time.Clock(span.Start)}–{time.Clock(stop)} ({Durations.ShortClock((stop - span.Start).TotalSeconds)})");
        else facts.Add(reading.Time is null ? "now" : $"since {time.Clock(span.Start)}{dayText}");
        string? problem = null;
        if (!adding && span.Start > now.AddSeconds(60)) problem = "A timer can’t start after now.";
        else if (adding && span.Start > now) problem = "That starts after now.";
        else if (owner.Rules.IsLocked(day)) problem = owner.Rules.LockMessage(time);
        else if (owner.ReadOnlyOrganization is not null) problem = "This workspace is read-only, so time can't be added or changed.";
        if (owner.Running is { } running && !adding)
            facts.Add(span.Start > running.Start ? $"stops “{(running.Description.Length == 0 ? "No description" : running.Description)}”" + (now - span.Start > TimeSpan.FromMinutes(1) ? $" at {time.Clock(span.Start)}" : "") : $"stops “{running.Description}”, which then has no time");
        var missing = adding ? owner.Rules.Missing(description, projectId, taskId, tags, false, project) : Array.Empty<(string Field, string Message)>();
        if (problem is null && missing.Count > 0)
            facts.Add(missing[0].Field switch { "description" => "Needs a description", "projectId" => "Needs a project", "taskId" => "Needs a task", _ => "Needs a tag" });
        if (span.Stop is { } end && end - span.Start > TimeSpan.FromHours(12)) facts.Add("More than 12 hours. Check the times.");
        if (problem is not null) facts.Add(problem);
        var primary = problem is not null ? "Can’t save" : missing.Count > 0 ? missing[0].Field switch
        {
            "projectId" => "Choose a project",
            "taskId" => "Choose a task",
            "tagIds" => "Add a tag",
            _ => "Describe it",
        } : adding ? "Add entry" : "Start timer";
        return new QuickRow("sentence.entry", title, string.Join(" · ", facts), primary, async () =>
        {
            if (problem is not null) throw new InvalidOperationException(problem);
            if (missing.Count > 0)
            {
                var next = missing[0].Field switch { "projectId" or "taskId" => "@", "tagIds" => "#", _ => "" };
                input.Text = (input.Text ?? "").TrimEnd() + (next.Length > 0 ? " " + next : "");
                input.CaretIndex = input.Text.Length;
                throw new KeepOpen();
            }
            var billable = reading.Billable || (project?.IsBillable ?? false);
            if (adding)
            {
                await owner.MutateAsync(() => owner.CreateEntryAsync(new CreateTimeEntryRequest
                {
                    Id = Guid.NewGuid(), Description = description, ProjectId = projectId, TaskId = taskId, TagIds = tags, IsBillable = billable, Start = span.Start, Stop = span.Stop,
                }));
                Done($"Added {(description.Length > 0 ? $"“{description}”" : "time")} · {time.Clock(span.Start)}–{time.Clock(span.Stop!.Value)}");
            }
            else
            {
                await owner.StartAsync(new CreateTimeEntryRequest { Description = description, ProjectId = projectId, TaskId = taskId, TagIds = tags, IsBillable = billable, Start = reading.Time is null ? null : span.Start });
                Done($"Timer started{(description.Length > 0 ? $" “{description}”" : "")}{(reading.Time is null ? "" : $" since {time.Clock(span.Start)}")}");
            }
        }, chips.Count > 0 || reading.Time is not null ? "Start" : null, Ui.Icon(adding ? "IconAdd" : "IconPlay", "brand", "small"), Blocked: problem is not null);
    }

    private IReadOnlyList<QuickRow> MentionRows(Mention mention, string text)
    {
        var query = mention.Query;
        var result = new List<QuickRow>();
        void Pick(Chip chip)
        {
            chips.RemoveAll(c => chip is Chip.Project or Chip.Client ? c is Chip.Project or Chip.Client : c == chip);
            chips.Add(chip);
            var (rest, caret) = Removing(mention, input.Text ?? "");
            input.Text = rest;
            input.CaretIndex = caret;
        }
        bool Matches(string name) => query.Length == 0 || name.Contains(query, StringComparison.CurrentCultureIgnoreCase);
        if (mention.Sign == '@')
        {
            foreach (var project in owner.Catalog.Projects.Where(p => p.ArchivedAt is null && Matches(p.Name)).OrderBy(p => p.Name).Take(8))
            {
                result.Add(new QuickRow("mention:project:" + project.Id, project.Name, owner.Catalog.Client(project.ClientId)?.Name, "Pick project", () =>
                {
                    Pick(new Chip.Project(project.Id.ToString(), null));
                    return Task.CompletedTask;
                }, "Project", Ui.Dot(project.Color, 8), KeepOpen: true));
                foreach (var task in project.Tasks.Where(t => !t.IsDone && query.Length > 0 && Matches(t.Name)))
                    result.Add(new QuickRow("mention:task:" + task.Id, $"{project.Name} · {task.Name}", null, "Pick task", () =>
                    {
                        Pick(new Chip.Project(project.Id.ToString(), task.Id.ToString()));
                        return Task.CompletedTask;
                    }, "Project", Ui.Dot(project.Color, 8), KeepOpen: true));
            }
            if (query.Length > 0)
                foreach (var client in owner.Catalog.Clients.Where(c => c.ArchivedAt is null && Matches(c.Name)).Take(3))
                    result.Add(new QuickRow("mention:client:" + client.Id, client.Name, "Client, for a report", "Pick client", () =>
                    {
                        Pick(new Chip.Client(client.Id.ToString()));
                        return Task.CompletedTask;
                    }, "Project", Ui.Icon("IconBuildingMultiple", "secondary", "small"), KeepOpen: true));
        }
        else
        {
            foreach (var tag in owner.Catalog.SortedTags.Where(t => Matches(t.Name)).Take(8))
                result.Add(new QuickRow("mention:tag:" + tag.Id, "#" + tag.Name, null, "Add tag", () =>
                {
                    Pick(new Chip.Tag(tag.Id.ToString()));
                    return Task.CompletedTask;
                }, "Tag", Ui.Icon("IconTag", "secondary", "small"), KeepOpen: true));
            if (query.Length > 0 && owner.Rules.CanCreateTags && !owner.Catalog.Tags.Any(t => string.Equals(t.Name, query, StringComparison.CurrentCultureIgnoreCase)))
                result.Add(new QuickRow("mention:create", $"Create tag “{query}”", null, "Create and add", async () =>
                {
                    var tag = await owner.CreateTagAsync(query);
                    Pick(new Chip.Tag(tag.Id.ToString()));
                }, "Tag", Ui.Icon("IconAdd", "secondary", "small"), KeepOpen: true));
        }
        var line = Removing(mention, text).Text;
        result.Add(Preview(Read(line, chips, SentenceOptions)) with { Section = "Start" });
        return result;
    }

    // Keys and running rows

    private sealed class KeepOpen : Exception;

    private async void OnKey(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down when rows.Count > 0:
                selected = (selected + 1) % rows.Count;
                Render();
                break;
            case Key.Up when rows.Count > 0:
                selected = (selected - 1 + rows.Count) % rows.Count;
                Render();
                break;
            case Key.Enter when rows.Count > 0:
                await RunAsync(rows[selected]);
                break;
            case Key.Tab when CurrentMention is not null && rows.Count > 0 && rows[selected].Id.StartsWith("mention:", StringComparison.Ordinal):
                await RunAsync(rows[selected]);
                break;
            case Key.Escape:
                Hide();
                break;
            case Key.Back when input.CaretIndex == 0 && (input.SelectionEnd - input.SelectionStart) == 0 && chips.Count > 0:
                RevertLastChip();
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    /// <summary>Backspace at the start turns the last chip back into text, to change it.</summary>
    private void RevertLastChip()
    {
        var chip = chips[^1];
        chips.RemoveAt(chips.Count - 1);
        var token = chip switch
        {
            Chip.Project p => "@" + (owner.Catalog.Task(p.TaskId is null ? null : Guid.Parse(p.TaskId))?.Name ?? owner.Catalog.Project(Guid.Parse(p.ProjectId))?.Name ?? ""),
            Chip.Client c => "@" + (owner.Catalog.Client(Guid.Parse(c.ClientId))?.Name ?? ""),
            Chip.Tag t => "#" + (owner.Catalog.Tag(Guid.Parse(t.TagId))?.Name ?? ""),
            _ => "$",
        };
        var rest = input.Text ?? "";
        input.Text = rest.Length > 0 ? token + " " + rest : token;
        input.CaretIndex = token.Length;
    }

    private async Task RunAsync(QuickRow row)
    {
        try
        {
            await row.Run();
            if (row.KeepOpen) Render();
            else Hide();
        }
        catch (KeepOpen)
        {
            Render();
        }
        catch (Exception exception)
        {
            error = exception.Message.Length > 0 ? exception.Message : "That didn’t work.";
            Render();
        }
    }

    private void Done(string summary)
    {
        if (!owner.IsActive) Toasts.Show(ToastKind.QuickEntry, summary, "From quick entry.", []);
        else owner.Notify(summary, NoticeKind.Success);
    }
}
