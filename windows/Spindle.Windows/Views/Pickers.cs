using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using Spindle.Core;
using Spindle.Windows.Model;

namespace Spindle.Windows.Views;

/// <summary>A row of a picker: a heading, or a choice that runs <see cref="Pick"/>.</summary>
internal sealed record PickerRow(string Id, Control Content, bool Checked = false, Func<Task>? Pick = null)
{
    public bool IsChoice => Pick is not null;
    public static PickerRow Header(string id, string text) => new(id, Ui.Text(text, "sectionHeader").Margin(8, 8, 0, 2));
}

/// <summary>
/// A list to choose from, narrowed by typing: the arrow keys move through it, Return picks and Escape closes, as the
/// Mac's project and tag pickers. Used in flyouts.
/// </summary>
internal sealed class PickerList : UserControl
{
    private readonly Func<string, IReadOnlyList<PickerRow>> rows;
    private readonly Func<string, string> empty;
    private readonly StackPanel list = new() { Spacing = 1, Margin = new Thickness(4) };
    private readonly ScrollViewer scroller;
    private IReadOnlyList<PickerRow> shown = [];
    private string? highlighted;

    public PickerList(string placeholder, Func<string, IReadOnlyList<PickerRow>> rows, Func<string, string> empty, double width = 340, double height = 380)
    {
        this.rows = rows;
        this.empty = empty;
        Width = width;
        Search = new TextBox { PlaceholderText = placeholder, Name = "PickerSearch" }.Classed("bare");
        var header = Ui.Columns("Auto,*", 8, Ui.Icon("IconSearch", "secondary").Centered(), Search);
        header.Margin = new Thickness(12, 10);
        scroller = new ScrollViewer { Content = list, MaxHeight = height - 50 };
        Content = new DockPanel { Children = { header.Dock(Dock.Top), Ui.Divider().Dock(Dock.Top), scroller } };
        Search.TextChanged += (_, _) =>
        {
            highlighted = null;
            Render();
        };
        Search.KeyDown += OnKey;
        AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(() => Search.Focus());
        Render();
    }

    public TextBox Search { get; }
    public event Action? Done;

    public void Close() => Done?.Invoke();

    public void Render()
    {
        shown = rows((Search.Text ?? "").Trim());
        list.Children.Clear();
        foreach (var row in shown)
        {
            if (!row.IsChoice)
            {
                list.Children.Add(row.Content);
                continue;
            }
            var check = row.Checked ? Ui.Icon("IconCheckmark", "brand", "small").Centered() : null;
            var button = new Button { Content = Ui.Columns("*,Auto", 8, row.Content, check), Padding = new Thickness(8, 5), Tag = row.Id }
                .Classed("row");
            if (row.Id == highlighted) button.Classes.Add("highlighted");
            if (row.Content is TextBlock { Text: { } label }) button.Spoken(label);
            button.PointerEntered += (_, _) => Highlight(row.Id);
            button.Click += async (_, _) => await PickAsync(row);
            list.Children.Add(button);
        }
        if (!shown.Any(r => r.IsChoice)) list.Children.Add(Ui.Text(empty((Search.Text ?? "").Trim()), "secondary").Margin(8, 8, 8, 8));
    }

    private void Highlight(string? id)
    {
        highlighted = id;
        foreach (var button in list.Children.OfType<Button>())
        {
            if (Equals(button.Tag, id))
            {
                button.Classes.Add("highlighted");
                button.BringIntoView();
            }
            else
            {
                button.Classes.Remove("highlighted");
            }
        }
    }

    private async Task PickAsync(PickerRow row)
    {
        if (row.Pick is null) return;
        try
        {
            await row.Pick();
        }
        catch (Exception exception)
        {
            Search.Text = "";
            list.Children.Insert(0, Ui.Wrapped(exception.Message, "critical", "caption").Margin(8, 4, 8, 4));
        }
    }

    private async void OnKey(object? sender, KeyEventArgs e)
    {
        var choices = shown.Where(r => r.IsChoice).ToArray();
        var index = Array.FindIndex(choices, r => r.Id == highlighted);
        switch (e.Key)
        {
            case Key.Down when choices.Length > 0:
                Highlight(choices[Math.Min(index + 1, choices.Length - 1)].Id);
                break;
            case Key.Up when choices.Length > 0:
                Highlight(choices[Math.Max((index < 0 ? choices.Length : index) - 1, 0)].Id);
                break;
            case Key.Enter:
                var pick = index >= 0 ? choices[index] : choices.Where(c => c.Id != "create").Take(2).Count() == 1 ? choices.First(c => c.Id != "create") : null;
                if (pick is not null) await PickAsync(pick);
                break;
            case Key.Escape:
                Close();
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    /// <summary>Shows a picker in a flyout under <paramref name="anchor"/>; picking closes it.</summary>
    public static Flyout Show(Control anchor, PickerList picker)
    {
        var flyout = new Flyout { Content = picker, Placement = PlacementMode.BottomEdgeAlignedLeft };
        flyout.FlyoutPresenterClasses.Add("picker");
        picker.Done += flyout.Hide;
        flyout.ShowAt(anchor);
        return flyout;
    }
}

/// <summary>Picks a project or one of its tasks, grouped by client; a new project can be created on the spot.</summary>
internal static class ProjectPicker
{
    public static PickerList Create(
        MainWindow owner,
        Guid? projectId,
        Guid? taskId,
        Func<Guid?, Guid?, Task> onChange,
        Func<Guid?, Guid?, bool>? taken = null,
        bool choosingAnew = false,
        string placeholder = "Search projects, tasks or clients",
        Func<Task>? onSplit = null,
        Func<Task>? onIgnore = null)
    {
        taken ??= (_, _) => false;
        PickerList? picker = null;
        async Task Choose(Guid? project, Guid? task)
        {
            picker!.Close();
            await onChange(project, task);
        }
        IReadOnlyList<PickerRow> Rows(string term)
        {
            bool Matches(params string[] words) => term.Length == 0 || words.Any(w => w.Contains(term, StringComparison.CurrentCultureIgnoreCase));
            var rows = new List<PickerRow>();
            // What agents are at work in now comes first, while nothing is typed.
            if (term.Length == 0 && !choosingAnew)
            {
                var now = AgentsNow(owner, taken).Select(a => new PickerRow($"agents:{a.Project.Id}:{a.Task?.Id}",
                    IconRow(Ui.Dot(a.Project.Color), Ui.Row(4, Ui.Text(a.Project.Name), a.Task is { } task ? Ui.Text("· " + task.Name, "secondary") : null)),
                    false, () => Choose(a.Project.Id, a.Task?.Id))).ToList();
                if (now.Count > 0)
                {
                    var header = Ui.Row(5, Ui.Icon("IconSparkle", "brand", "small").Centered(), Ui.Text("Agents now", "sectionHeader").Centered())
                        .Tip("Projects agents worked in during the last 15 minutes, from your linked repositories");
                    rows.Add(new PickerRow("agents", header.Margin(8, 6, 0, 2)));
                    rows.AddRange(now);
                    rows.Add(new PickerRow("agents-end", Ui.Divider().Margin(8, 4, 8, 4)));
                }
            }
            if (Matches("No project") && !taken(null, null))
                rows.Add(new PickerRow("none", IconRow("IconFolder", Ui.Text("No project", "secondary"), true), projectId is null && !choosingAnew, () => Choose(null, null)));
            // Choosing a project for a repository: ignoring it instead sits beside "No project".
            if (onIgnore is not null && Matches("Ignore repository"))
                rows.Add(new PickerRow("ignore", IconRow("IconEyeOff", Ui.Text("Ignore repository", "secondary"), true), false, async () =>
                {
                    picker!.Close();
                    await onIgnore();
                }));
            foreach (var group in owner.Catalog.ProjectGroups(projectId))
            {
                var clientName = group.Client?.Name ?? "";
                var groupRows = new List<PickerRow>();
                foreach (var project in group.Projects)
                {
                    var tasks = project.Tasks.Where(t => (!t.IsDone || t.Id == taskId) && Matches(t.Name, project.Name, clientName) && !taken(project.Id, t.Id)).OrderBy(t => t.Name).ToArray();
                    var free = !taken(project.Id, null);
                    if (!(Matches(project.Name, clientName) && free) && tasks.Length == 0) continue;
                    if (free)
                    {
                        var name = Ui.Text(project.Name);
                        groupRows.Add(new PickerRow(project.Id.ToString(), IconRow(Ui.Dot(project.Color), name), project.Id == projectId && taskId is null && !choosingAnew, () => Choose(project.Id, null)));
                    }
                    foreach (var task in tasks)
                    {
                        var label = Ui.Text(task.Name, "secondary").Margin(25, 0, 0, 0);
                        groupRows.Add(new PickerRow(task.Id.ToString(), label, task.Id == taskId && !choosingAnew, () => Choose(project.Id, task.Id)));
                    }
                }
                if (groupRows.Count == 0) continue;
                rows.Add(PickerRow.Header("client:" + group.Client?.Id, group.Client?.Name ?? "No client"));
                rows.AddRange(groupRows);
            }
            if (onSplit is not null && term.Length == 0)
                rows.Add(new PickerRow("split", IconRow("IconArrowSplit", Ui.Text("Split time between projects…")), false, async () =>
                {
                    picker!.Close();
                    await onSplit();
                }));
            if (owner.Rules.CanCreateProjects)
                rows.Add(new PickerRow("create", IconRow("IconAdd", Ui.Text(term.Length == 0 ? "Create a new project" : $"Create project “{term}”")), false, async () =>
                {
                    if (term.Length == 0) throw new InvalidOperationException("Type the new project's name in the search field first.");
                    var created = await owner.CreateProjectAsync(term);
                    await Choose(created.Id, null);
                }));
            return rows;
        }
        picker = new PickerList(placeholder, Rows, _ => "No matching projects.");
        if (!choosingAnew && owner.AgentLogsEnabled)
        {
            // Today's activity read again if it is a few minutes old, then "Agents now" follows it.
            picker.AttachedToVisualTree += async (_, _) =>
            {
                try
                {
                    await owner.Splits.LoadNowAsync();
                }
                catch (Exception)
                {
                    return;
                }
                if (picker.Search.Text is null or "") picker.Render();
            };
        }
        return picker;
    }

    /// <summary>The projects agents are at work in now, each with the task a branch rule names, unless taken already.</summary>
    internal static List<(ProjectResponse Project, TaskResponse? Task)> AgentsNow(MainWindow owner, Func<Guid?, Guid?, bool> taken)
    {
        var catalog = owner.Catalog;
        var now = new List<(ProjectResponse, TaskResponse?)>();
        foreach (var placement in owner.Splits.AgentsNow())
        {
            if (catalog.Project(placement.ProjectId) is not { ArchivedAt: null } project) continue;
            var task = catalog.Task(placement.TaskId) is { IsDone: false } named && named.ProjectId == project.Id ? named : null;
            if (!taken(project.Id, task?.Id)) now.Add((project, task));
        }
        return now;
    }

    /// <summary>An icon in a column of its own width, so every row's text starts in the same place.</summary>
    internal static Control IconRow(Control icon, Control text, bool secondary = false)
    {
        var holder = new Border { Width = 18, Child = icon };
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        icon.VerticalAlignment = VerticalAlignment.Center;
        text.VerticalAlignment = VerticalAlignment.Center;
        return Ui.Columns("Auto,*", 7, holder, text);
    }

    internal static Control IconRow(string icon, Control text, bool secondary = false) =>
        IconRow(Ui.Icon(icon, secondary ? "secondary" : "", "small"), text, secondary);
}

/// <summary>Picks any number of tags; a name that doesn't exist yet can be created. The list stays open to pick more.</summary>
internal static class TagPicker
{
    public static PickerList Create(MainWindow owner, Guid[] tagIds, Func<Guid[], Task> onChange)
    {
        var chosen = tagIds.ToList();
        PickerList? picker = null;
        async Task Toggle(Guid id)
        {
            if (!chosen.Remove(id)) chosen.Add(id);
            await onChange(chosen.ToArray());
            picker!.Render();
        }
        IReadOnlyList<PickerRow> Rows(string name)
        {
            var rows = owner.Catalog.SortedTags
                .Where(t => name.Length == 0 || t.Name.Contains(name, StringComparison.CurrentCultureIgnoreCase))
                .Select(t => new PickerRow(t.Id.ToString(), Ui.Text(t.Name), chosen.Contains(t.Id), () => Toggle(t.Id)))
                .ToList();
            var exists = owner.Catalog.Tags.Any(t => string.Equals(t.Name, name, StringComparison.CurrentCultureIgnoreCase));
            if (owner.Rules.CanCreateTags && name.Length > 0 && !exists)
                rows.Add(new PickerRow("create", ProjectPicker.IconRow("IconAdd", Ui.Text($"Create tag “{name}”")), false, async () =>
                {
                    var tag = await owner.CreateTagAsync(name);
                    chosen.Add(tag.Id);
                    await onChange(chosen.ToArray());
                    picker!.Search.Text = "";
                }));
            return rows;
        }
        string Empty(string name) => name.Length > 0 ? "No matching tags." : owner.Rules.CanCreateTags ? "No tags yet. Type a name to create one." : "No tags yet.";
        picker = new PickerList(owner.Rules.CanCreateTags ? "Search or create tags" : "Search tags", Rows, Empty, 280, 320);
        return picker;
    }
}
