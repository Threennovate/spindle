using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Spindle.Core;
using Spindle.Windows.Model;
using Spindle.Windows.Views.Shell;

namespace Spindle.Windows.Views;

/// <summary>What the catalog pages share: a title, a toolbar, and a table of rows with context menus.</summary>
public abstract class CatalogPage : UserControl, ILoadable
{
    protected readonly MainWindow owner;
    private readonly ContentControl body = new();

    protected CatalogPage(MainWindow owner, string title, string subtitle)
    {
        this.owner = owner;
        var header = Ui.Stack(2, Ui.Text(title, "title"), Ui.Wrapped(subtitle, "secondary"));
        var top = Ui.Stack(14, header, Toolbar());
        top.Margin = new Thickness(32, 20, 32, 12);
        body.Margin = new Thickness(32, 4, 32, 32);
        Content = new DockPanel { Children = { top.Dock(Dock.Top), new ScrollViewer { Content = body } } };
    }

    protected abstract Control Toolbar();
    protected abstract void Render();

    public virtual async Task LoadAsync()
    {
        try
        {
            await owner.ReloadCatalogAsync();
        }
        catch (Exception exception)
        {
            owner.Notify(exception.Message);
        }
        Render();
    }

    /// <summary>Runs a change, shows what happened, and reloads the catalog everywhere.</summary>
    protected async Task Change(Func<Task> change, string? done, string failure)
    {
        try
        {
            await change();
            await owner.ReloadCatalogAsync();
            if (done is not null) owner.Notify(done, NoticeKind.Success);
        }
        catch (Exception exception)
        {
            owner.Notify(exception.Message.Length > 0 ? exception.Message : failure);
        }
        Render();
    }

    protected void ShowEmpty(string icon, string title, string text)
    {
        var stack = Ui.Stack(8, Ui.Icon(icon, "secondary").Classed("large"), Ui.Text(title, "bodyLarge"), Ui.Wrapped(text, "secondary"));
        foreach (var child in stack.Children) child.HorizontalAlignment = HorizontalAlignment.Center;
        ((TextBlock)stack.Children[2]).TextAlignment = TextAlignment.Center;
        stack.MaxWidth = 420;
        stack.Margin = new Thickness(0, 60);
        body.Content = stack;
    }

    /// <summary>A table: a heading row and a card of rows.</summary>
    protected void ShowTable(string columns, string[] headings, IEnumerable<(Control[] Cells, MenuFlyout? Menu, Func<Task>? Open)> items)
    {
        var rows = new StackPanel();
        var heading = Ui.Columns(columns + ",Auto", 12, headings.Select((h, i) => (Control)(i > 0 && h is "Tracked" or "Tasks" ? Right(Ui.Text(h, "caption", "medium", "secondary")) : Ui.Text(h, "caption", "medium", "secondary"))).Append(new Border { Width = 24 }).ToArray());
        heading.Margin = new Thickness(16, 0, 10, 6);
        var first = true;
        foreach (var (cells, menu, open) in items)
        {
            if (!first) rows.Children.Add(Ui.Divider().Margin(16, 0, 0, 0));
            first = false;
            var more = new Button { Content = Ui.Icon("IconMoreHorizontal"), Flyout = menu, IsVisible = menu is not null }.Classed("icon", "small", "reveal").Tip("More").Spoken("More options");
            var row = new Border { Child = Ui.Columns(columns + ",Auto", 12, [.. cells, more]) }.Classed("listRow");
            row.Cursor = new Cursor(open is null ? StandardCursorType.Arrow : StandardCursorType.Hand);
            if (menu is not null) row.ContextFlyout = Copy(menu);
            if (open is not null) row.DoubleTapped += async (_, _) => await open();
            rows.Children.Add(row);
        }
        var card = Ui.Card(rows, "flush");
        card.ClipToBounds = true;
        body.Content = Ui.Stack(0, heading, card);
    }

    /// <summary>The same menu for the row's context menu and its "more" button; a flyout can show in one place at a time.</summary>
    private static MenuFlyout Copy(MenuFlyout source)
    {
        var copy = new MenuFlyout();
        foreach (var item in source.Items)
        {
            if (item is MenuItem { } menuItem)
            {
                var clone = new MenuItem { Header = menuItem.Header, IsEnabled = menuItem.IsEnabled, Icon = menuItem.Tag is string icon ? Ui.Icon(icon) : null };
                clone.Click += (_, e) => menuItem.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
                copy.Items.Add(clone);
            }
            else
            {
                copy.Items.Add(new Separator());
            }
        }
        return copy;
    }

    protected static MenuItem Item(string header, string icon, Func<Task> action, bool enabled = true)
    {
        var item = new MenuItem { Header = header, Icon = Ui.Icon(icon), IsEnabled = enabled, Tag = icon };
        item.Click += async (_, _) => await action();
        return item;
    }

    protected static Control Right(TextBlock text)
    {
        text.HorizontalAlignment = HorizontalAlignment.Right;
        text.VerticalAlignment = VerticalAlignment.Center;
        return text;
    }

    /// <summary>A name in a small dialog: new or renamed client, tag or task.</summary>
    protected Task<string?> AskName(string title, string placeholder, string saveLabel, string current = "") => NameSheet.Ask(owner, title, placeholder, saveLabel, current);
}

/// <summary>Projects, grouped by client, with their tasks, estimates and time, as on the Mac.</summary>
public sealed class ProjectsView : CatalogPage
{
    private Segmented? status;
    private TextBox? search;
    private ComboBox? client;
    private Guid?[] clientChoices = [];

    public ProjectsView(MainWindow owner) : base(owner, "Projects", "Group your time by project, and by task within a project") { }

    protected override Control Toolbar()
    {
        status = new Segmented([("Active", "Active"), ("Archived", "Archived"), ("All", "All")], 0, _ => Render());
        search = new TextBox { PlaceholderText = "Search", Width = 240 }.Tip("Search projects or clients").Spoken("Search projects or clients");
        search.TextChanged += (_, _) => Render();
        client = new ComboBox { MinWidth = 160 };
        client.SelectionChanged += (_, _) => Render();
        var create = Ui.Button(Ui.Labeled("IconAdd", "New project"), () => ProjectEditor.Show(owner, null, Render), owner.Notify, "primary").Spoken("New project").Named("NewProject");
        create.IsVisible = owner.Rules.CanCreateProjects;
        return Ui.Columns("Auto,Auto,Auto,*,Auto", 12, status, search, client, null, create);
    }

    protected override void Render()
    {
        var catalog = owner.Catalog;
        if (client is not null && clientChoices.Length != catalog.Clients.Length + 2)
        {
            clientChoices = [Guid.NewGuid(), null, .. catalog.Clients.OrderBy(c => c.Name).Select(c => (Guid?)c.Id)];
            client.ItemsSource = new[] { "All clients", "No client" }.Concat(catalog.Clients.OrderBy(c => c.Name).Select(c => c.Name)).ToArray();
            client.SelectedIndex = 0;
        }
        if (catalog.Projects.Length == 0)
        {
            ShowEmpty("IconFolder", "No projects yet", "Projects group your time, and tasks within them.");
            return;
        }
        var term = search?.Text?.Trim() ?? "";
        var shown = catalog.Projects
            .Where(p => status?.Selected switch { 1 => p.ArchivedAt is not null, 2 => true, _ => p.ArchivedAt is null })
            .Where(p => term.Length == 0 || p.Name.Contains(term, StringComparison.CurrentCultureIgnoreCase) || catalog.Client(p.ClientId)?.Name.Contains(term, StringComparison.CurrentCultureIgnoreCase) == true)
            .Where(p => client is null || client.SelectedIndex <= 0 || p.ClientId == clientChoices[client.SelectedIndex])
            .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        if (shown.Length == 0)
        {
            ShowEmpty("IconFolder", "No projects match", "Try another search or client.");
            return;
        }
        ShowTable("*,160,80,90,210", ["Project", "Client", "Tasks", "Tracked", "Estimate"], shown.Select(project =>
        {
            var name = Ui.Row(8, Ui.Dot(project.Color, 10), Ui.Text(project.Name, "medium").Centered());
            if (project.IsPrivate) name.Children.Add(Ui.Icon("IconLockClosed", "secondary", "small").Centered().Tip("Private"));
            if (project.IsBillable) name.Children.Add(Ui.Icon("IconMoney", "secondary", "small").Centered().Tip("Billable"));
            if (project.ArchivedAt is not null) name.Children.Add(new Border { Child = Ui.Text("Archived", "caption") }.Classed("chip"));
            var open = project.Tasks.Count(t => !t.IsDone);
            Control[] cells =
            [
                name.Centered(),
                Ui.Text(catalog.Client(project.ClientId)?.Name ?? "—", catalog.Client(project.ClientId) is null ? "secondary" : "").Centered(),
                Right(Ui.Text(open > 0 ? $"{open} open" : "—", "secondary")),
                Right(Ui.Text(Durations.HoursMinutes(project.TrackedSeconds), "mono")),
                Estimate(project.TrackedSeconds, project.EstimateSeconds).Margin(12, 0, 0, 0),
            ];
            var menu = new MenuFlyout();
            menu.Items.Add(Item("Tasks…", "IconTextBulletListAdd", () => ProjectTasks.Show(owner, project.Id, Render)));
            if (project.CanManage)
            {
                menu.Items.Add(Item("Edit…", "IconEdit", () => ProjectEditor.Show(owner, project, Render)));
                menu.Items.Add(Item(project.ArchivedAt is null ? "Archive" : "Restore", "IconArchive", () => Change(
                    () => owner.Api!.ArchiveProjectAsync(owner.WorkspaceId, project.Id, project.ArchivedAt is not null),
                    project.ArchivedAt is null ? $"“{project.Name}” archived" : $"“{project.Name}” restored", "Couldn't change the project.")));
                menu.Items.Add(new Separator());
                menu.Items.Add(Item("Delete…", "IconDelete", () => DeleteAsync(project)));
            }
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Members and rates on the web…", "IconOpen", () =>
            {
                owner.OpenWeb($"projects/{project.Id}");
                return Task.CompletedTask;
            }));
            (Control[] Cells, MenuFlyout? Menu, Func<Task>? Open) item = (cells, menu, () => ProjectTasks.Show(owner, project.Id, Render));
            return item;
        }));
    }

    private async Task DeleteAsync(ProjectResponse project)
    {
        var message = project.TrackedSeconds > 0
            ? $"Its tasks are deleted. The {Durations.ShortClock(project.TrackedSeconds)} tracked on it stay, without a project. To keep the project in reports, archive it instead."
            : "Its tasks are deleted too.";
        if (!await owner.ConfirmAsync($"Delete “{project.Name}”?", message, "Delete project", destructive: true)) return;
        await Change(() => owner.Api!.DeleteProjectAsync(owner.WorkspaceId, project.Id), $"“{project.Name}” deleted", "Couldn't delete the project.");
    }

    /// <summary>"12h 00m of 40h 00m" beside a bar, red once over the estimate; a dash without one.</summary>
    internal static Control Estimate(long tracked, long? estimate)
    {
        if (estimate is not { } total || total <= 0) return Ui.Text("—", "secondary").Centered();
        var over = tracked > total;
        var fraction = Math.Clamp((double)tracked / total, 0, 1);
        var track = new Grid { Width = 80, Height = 6, VerticalAlignment = VerticalAlignment.Center };
        track.ColumnDefinitions.Add(new ColumnDefinition(Math.Max(fraction, 0.0001), GridUnitType.Star));
        track.ColumnDefinitions.Add(new ColumnDefinition(Math.Max(1 - fraction, 0.0001), GridUnitType.Star));
        var back = new Border { CornerRadius = new CornerRadius(3) }.Res(Border.BackgroundProperty, "ChipBackgroundBrush");
        Grid.SetColumnSpan(back, 2);
        track.Children.Add(back);
        track.Children.Add(new Border { CornerRadius = new CornerRadius(3) }.Res(Border.BackgroundProperty, over ? "CriticalBrush" : "BrandBrush"));
        var text = Ui.Text($"{Durations.HoursMinutes(tracked)} of {Durations.HoursMinutes(total)}", "caption", "mono", over ? "critical" : "secondary");
        return Ui.Row(8, track, text.Centered());
    }
}

/// <summary>A new or changed project: name, client, colour, estimate, rate, billable and private.</summary>
internal static class ProjectEditor
{
    public static async Task Show(MainWindow owner, ProjectResponse? project, Action done)
    {
        var catalog = owner.Catalog;
        var name = new TextBox { Text = project?.Name ?? "", Name = "ProjectName" };
        var clients = catalog.Clients.Where(c => c.ArchivedAt is null || c.Id == project?.ClientId).OrderBy(c => c.Name).ToArray();
        var clientNames = new List<string> { "No client" };
        clientNames.AddRange(clients.Select(c => c.Name));
        if (owner.IsAdmin) clientNames.Add("New client…");
        var client = new ComboBox { ItemsSource = clientNames, SelectedIndex = project?.ClientId is { } id ? Array.FindIndex(clients, c => c.Id == id) + 1 : 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        var newClient = new TextBox { PlaceholderText = "Client name", IsVisible = false };
        client.SelectionChanged += (_, _) => newClient.IsVisible = owner.IsAdmin && client.SelectedIndex == clientNames.Count - 1;
        var color = project?.Color is { Length: > 0 } existing ? existing : catalog.NextColor;
        var swatches = new WrapPanel();
        void RenderSwatches()
        {
            swatches.Children.Clear();
            foreach (var hex in Catalog.Colors)
            {
                var chosen = string.Equals(hex, color, StringComparison.OrdinalIgnoreCase);
                var swatch = new Button
                {
                    Width = 22,
                    Height = 22,
                    MinHeight = 0,
                    Padding = new Thickness(0),
                    CornerRadius = new CornerRadius(11),
                    Background = Ui.ProjectBrush(hex),
                    BorderThickness = new Thickness(0),
                    Margin = new Thickness(0, 0, 5, 5),
                    Content = chosen ? Ui.Icon("IconCheckmark", "small").Res(PathIcon.ForegroundProperty, "OnCriticalBrush") : null,
                }.Spoken(hex).Tip(hex);
                swatch.Click += (_, _) =>
                {
                    color = hex;
                    RenderSwatches();
                };
                swatches.Children.Add(swatch);
            }
        }
        RenderSwatches();
        var currency = owner.Rules.Settings.Currency is { Length: > 0 } code ? code : "EUR";
        var estimate = new TextBox { PlaceholderText = "Optional", Text = project?.EstimateSeconds is { } seconds ? (seconds / 3600m).ToString("0.##", CultureInfo.CurrentCulture) : "" };
        var rate = new TextBox { PlaceholderText = owner.Rules.Settings.DefaultHourlyRate is { } fallback ? $"Member rates, or {ReportFormat.Money(fallback, currency)}" : "Member rates", Text = project?.HourlyRate?.ToString("0.##", CultureInfo.CurrentCulture) ?? "" };
        rate.IsEnabled = owner.IsAdmin;
        var billable = new ToggleSwitch { IsChecked = project?.IsBillable ?? true, OnContent = "", OffContent = "" };
        var isPrivate = new ToggleSwitch { IsChecked = project?.IsPrivate ?? false, OnContent = "", OffContent = "" };
        var error = Ui.Wrapped("", "critical");
        error.IsVisible = false;
        var nameField = new FormField("Name", name);
        var estimateField = new FormField("Estimate (hours)", estimate);
        var rateField = new FormField($"Hourly rate ({currency})", rate);
        Control Switch(string label, string hint, ToggleSwitch toggle) => Ui.Columns("*,Auto", 12, Ui.Stack(2, Ui.Text(label, "medium"), Ui.Wrapped(hint, "caption", "secondary")), toggle.Centered());
        var save = Ui.Button(project is null ? "Create project" : "Save", async () =>
        {
            nameField.Error = string.IsNullOrWhiteSpace(name.Text) ? "Enter a name." : null;
            decimal? hours = null;
            if ((estimate.Text ?? "").Trim().Length > 0)
            {
                if (decimal.TryParse(estimate.Text!.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var value) && value > 0) hours = value;
                else estimateField.Error = "Enter the hours, e.g. 40.";
            }
            else estimateField.Error = null;
            decimal? hourly = null;
            if ((rate.Text ?? "").Trim().Length > 0)
            {
                if (decimal.TryParse(rate.Text!.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var value) && value >= 0) hourly = value;
                else rateField.Error = "Enter an amount, e.g. 80.";
            }
            else rateField.Error = null;
            if (nameField.Error is not null || estimateField.Error is not null || rateField.Error is not null) return;
            try
            {
                Guid? clientId = client.SelectedIndex > 0 && client.SelectedIndex <= clients.Length ? clients[client.SelectedIndex - 1].Id : null;
                if (newClient.IsVisible)
                {
                    if (string.IsNullOrWhiteSpace(newClient.Text)) throw new InvalidOperationException("Enter the new client's name.");
                    clientId = (await owner.Api!.CreateClientAsync(owner.WorkspaceId, new ClientRequest { Name = newClient.Text.Trim() })).Id;
                }
                var request = new ProjectRequest
                {
                    Name = name.Text!.Trim(), ClientId = clientId, Color = color, IsBillable = billable.IsChecked == true, IsPrivate = isPrivate.IsChecked == true,
                    EstimateSeconds = hours is { } h ? (long)(h * 3600) : null, HourlyRate = owner.IsAdmin ? hourly : project?.HourlyRate,
                    BillingMode = project?.BillingMode, DailyHours = project?.DailyHours, SeparateBilledTime = project?.SeparateBilledTime,
                };
                if (project is null) await owner.Api!.CreateProjectAsync(owner.WorkspaceId, request);
                else await owner.Api!.UpdateProjectAsync(owner.WorkspaceId, project.Id, request);
                await owner.ReloadCatalogAsync();
                owner.Notify(project is null ? $"Project “{request.Name}” created" : "Project saved", NoticeKind.Success);
                owner.Dialogs.Close(true);
                done();
            }
            catch (Exception exception)
            {
                error.Text = exception.Message;
                error.IsVisible = true;
            }
        }, owner.Notify, "primary").Named("SaveProject");
        var form = Ui.Stack(16,
            Ui.Text(project is null ? "New project" : "Edit project", "subtitle"),
            error,
            nameField,
            new FormField("Client", Ui.Stack(8, client, newClient)),
            Ui.Stack(8, Ui.Text("Color", "medium"), swatches),
            Ui.Columns("*,*", 16, estimateField, rateField),
            Switch("Billable", "New time on this project is billable by default.", billable),
            Switch("Private", "Only project members and admins can see it.", isPrivate));
        await Dialog(owner, form, save, 480);
    }

    /// <summary>A form over the window with Cancel and its main button, as the Mac's sheets.</summary>
    internal static Task Dialog(MainWindow owner, Control form, Button main, double width)
    {
        form.Margin = new Thickness(24, 24, 24, 20);
        var cancel = Ui.Button("Cancel", () => owner.Dialogs.Close(false));
        var buttons = Ui.Row(8, cancel, main);
        buttons.HorizontalAlignment = HorizontalAlignment.Right;
        var footer = new Border { Child = buttons, Padding = new Thickness(24, 16) }.Res(Border.BackgroundProperty, "CardBackgroundSecondaryBrush");
        return owner.Dialogs.ShowAsync(new DockPanel { Children = { footer.Dock(Dock.Bottom), new ScrollViewer { Content = form, MaxHeight = 600 } } }, width);
    }
}

/// <summary>A project's tasks: done or open, estimates, renamed and added here.</summary>
internal static class ProjectTasks
{
    public static async Task Show(MainWindow owner, Guid projectId, Action done)
    {
        var list = Ui.Stack(0);
        var header = new ContentControl();
        var api = owner.Api!;
        ProjectResponse? Project() => owner.Catalog.Project(projectId);
        async Task Run(Func<Task> change, string failure)
        {
            try
            {
                await change();
                await owner.ReloadCatalogAsync();
            }
            catch (Exception exception)
            {
                owner.Notify(exception.Message.Length > 0 ? exception.Message : failure);
            }
            Render();
        }
        void Render()
        {
            list.Children.Clear();
            if (Project() is not { } project) return;
            header.Content = Ui.Columns("Auto,*,Auto", 10, Ui.Dot(project.Color, 12), Ui.Text(project.Name, "subtitle"), Ui.Text($"{Durations.ShortClock(project.TrackedSeconds)} tracked", "secondary").Centered());
            var tasks = project.Tasks.OrderBy(t => t.IsDone).ThenBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
            if (tasks.Length == 0) list.Children.Add(Ui.Wrapped("No tasks yet. Tasks split a project's time, e.g. into design and development.", "secondary").Margin(0, 20, 0, 20));
            foreach (var task in tasks)
            {
                var check = new CheckBox { IsChecked = task.IsDone, IsEnabled = project.CanManage, MinWidth = 0 }.Tip(task.IsDone ? "Done" : "Mark done");
                check.IsCheckedChanged += async (_, _) => await Run(() => api.UpdateTaskAsync(owner.WorkspaceId, project.Id, task.Id, new TaskRequest { Name = task.Name, EstimateSeconds = task.EstimateSeconds, IsDone = check.IsChecked == true }), "Couldn't change the task.");
                var name = Ui.Text(task.Name, task.IsDone ? "secondary" : "");
                if (task.IsDone) name.TextDecorations = TextDecorations.Strikethrough;
                var time = task.EstimateSeconds is null ? Ui.Text(Durations.HoursMinutes(task.TrackedSeconds), "secondary", "mono").Centered() : ProjectsView.Estimate(task.TrackedSeconds, task.EstimateSeconds);
                var row = new Border { Child = Ui.Columns("Auto,*,Auto", 8, check, name.Centered(), time), Padding = new Thickness(4, 4) };
                if (project.CanManage)
                {
                    var menu = new MenuFlyout();
                    var rename = new MenuItem { Header = "Rename…", Icon = Ui.Icon("IconEdit") };
                    rename.Click += async (_, _) =>
                    {
                        if (await NameSheet.Ask(owner, "Rename task", "", "Save", task.Name) is { } renamed)
                            await Run(() => api.UpdateTaskAsync(owner.WorkspaceId, project.Id, task.Id, new TaskRequest { Name = renamed, EstimateSeconds = task.EstimateSeconds, IsDone = task.IsDone }), "Couldn't change the task.");
                        await Task.Yield();
                    };
                    var toggle = new MenuItem { Header = task.IsDone ? "Mark not done" : "Mark done", Icon = Ui.Icon("IconCheckmark") };
                    toggle.Click += async (_, _) => await Run(() => api.UpdateTaskAsync(owner.WorkspaceId, project.Id, task.Id, new TaskRequest { Name = task.Name, EstimateSeconds = task.EstimateSeconds, IsDone = !task.IsDone }), "Couldn't change the task.");
                    var delete = new MenuItem { Header = "Delete", Icon = Ui.Icon("IconDelete") };
                    delete.Click += async (_, _) => await Run(async () =>
                    {
                        await api.DeleteTaskAsync(owner.WorkspaceId, project.Id, task.Id);
                        owner.Notify($"Task “{task.Name}” deleted", NoticeKind.Success);
                    }, "Couldn't delete the task.");
                    menu.Items.Add(rename);
                    menu.Items.Add(toggle);
                    menu.Items.Add(delete);
                    row.ContextFlyout = menu;
                }
                list.Children.Add(row);
            }
        }
        Render();
        var newTask = new TextBox { PlaceholderText = "New task" };
        var add = Ui.Button("Add task", async () =>
        {
            if (string.IsNullOrWhiteSpace(newTask.Text)) return;
            var text = newTask.Text.Trim();
            await Run(() => api.CreateTaskAsync(owner.WorkspaceId, projectId, new TaskRequest { Name = text }), "Couldn't add the task.");
            newTask.Text = "";
        }, owner.Notify);
        newTask.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            add.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            e.Handled = true;
        };
        var adding = Ui.Columns("*,Auto", 8, newTask, add);
        adding.IsVisible = owner.Catalog.Project(projectId)?.CanManage == true;
        var close = Ui.Button("Done", () => owner.Dialogs.Close(true), "primary");
        close.HorizontalAlignment = HorizontalAlignment.Right;
        var content = Ui.Stack(14, header, new ScrollViewer { Content = list, MaxHeight = 300 }, adding, close);
        content.Margin = new Thickness(24);
        await owner.Dialogs.ShowAsync(content, 520);
        done();
    }
}

/// <summary>Asks for a name: a new or renamed client, tag or task.</summary>
internal static class NameSheet
{
    public static async Task<string?> Ask(MainWindow owner, string title, string placeholder, string saveLabel, string current = "")
    {
        var name = new TextBox { PlaceholderText = placeholder, Text = current, Name = "SheetName" };
        var field = new FormField("Name", name);
        string? result = null;
        var save = Ui.Button(saveLabel, () =>
        {
            if (string.IsNullOrWhiteSpace(name.Text))
            {
                field.Error = "Enter a name.";
                return;
            }
            result = name.Text.Trim();
            owner.Dialogs.Close(true);
        }, "primary").Named("SheetSave");
        name.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            save.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            e.Handled = true;
        };
        name.AttachedToVisualTree += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            name.Focus();
            name.SelectAll();
        });
        await ProjectEditor.Dialog(owner, Ui.Stack(16, Ui.Text(title, "subtitle"), field), save, 400);
        return result;
    }
}

/// <summary>The people and companies the work is for.</summary>
public sealed class ClientsView : CatalogPage
{
    private CheckBox? archived;

    public ClientsView(MainWindow owner) : base(owner, "Clients", "The people and companies you work for. Assign them to projects.") { }

    protected override Control Toolbar()
    {
        archived = new CheckBox { Content = "Show archived" };
        archived.IsCheckedChanged += (_, _) => Render();
        var create = Ui.Button(Ui.Labeled("IconAdd", "New client"), async () =>
        {
            if (await AskName("New client", "e.g. Acme", "Add client") is { } name)
                await Change(() => owner.Api!.CreateClientAsync(owner.WorkspaceId, new ClientRequest { Name = name }), $"Client “{name}” added", "Couldn't change the client.");
        }, owner.Notify, "primary").Spoken("New client");
        create.IsVisible = owner.IsAdmin;
        return Ui.Columns("Auto,*,Auto", 12, archived.Centered(), null, create);
    }

    protected override void Render()
    {
        var clients = owner.Catalog.Clients.Where(c => archived?.IsChecked == true || c.ArchivedAt is null).OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        if (clients.Length == 0)
        {
            ShowEmpty("IconBuildingMultiple", "No clients yet", "The people and companies you work for. Assign them to projects to see and bill their time together.");
            return;
        }
        ShowTable("*,180", ["Client", "Projects"], clients.Select(client =>
        {
            var name = Ui.Row(8, Ui.Text(client.Name, "medium").Centered());
            if (client.ArchivedAt is not null) name.Children.Add(new Border { Child = Ui.Text("Archived", "caption") }.Classed("chip"));
            Control[] cells = [name.Centered(), Ui.Text(client.ActiveProjectCount == 1 ? "1 active project" : $"{client.ActiveProjectCount} active projects", "secondary").Centered()];
            MenuFlyout? menu = null;
            Func<Task>? open = null;
            if (owner.IsAdmin)
            {
                async Task Rename()
                {
                    if (await AskName("Rename client", "e.g. Acme", "Save", client.Name) is { } renamed)
                        await Change(() => owner.Api!.UpdateClientAsync(owner.WorkspaceId, client.Id, new ClientRequest { Name = renamed }), null, "Couldn't change the client.");
                }
                menu = new MenuFlyout();
                menu.Items.Add(Item("Rename…", "IconEdit", Rename));
                menu.Items.Add(Item(client.ArchivedAt is null ? "Archive" : "Restore", "IconArchive", () => Change(
                    () => owner.Api!.ArchiveClientAsync(owner.WorkspaceId, client.Id, client.ArchivedAt is not null), null, "Couldn't change the client.")));
                menu.Items.Add(new Separator());
                menu.Items.Add(Item("Delete…", "IconDelete", async () =>
                {
                    if (await owner.ConfirmAsync($"Delete “{client.Name}”?", "Its projects stay, without a client.", "Delete client", destructive: true))
                        await Change(() => owner.Api!.DeleteClientAsync(owner.WorkspaceId, client.Id), $"“{client.Name}” deleted", "Couldn't delete the client.");
                }));
                open = Rename;
            }
            return (cells, menu, open);
        }));
    }
}

/// <summary>Labels that cut across projects.</summary>
public sealed class TagsView : CatalogPage
{
    private TextBlock? count;

    public TagsView(MainWindow owner) : base(owner, "Tags", "Labels that cut across projects, such as meeting, review or overtime.") { }

    protected override Control Toolbar()
    {
        count = Ui.Text("", "secondary");
        var create = Ui.Button(Ui.Labeled("IconAdd", "New tag"), async () =>
        {
            if (await AskName("New tag", "e.g. meeting", "Add tag") is { } name)
                await Change(() => owner.CreateTagAsync(name), $"Tag “{name}” added", "Couldn't change the tag.");
        }, owner.Notify, "primary").Spoken("New tag");
        create.IsVisible = owner.Rules.CanCreateTags;
        return Ui.Columns("Auto,*,Auto", 12, count.Centered(), null, create);
    }

    protected override void Render()
    {
        var tags = owner.Catalog.SortedTags;
        if (count is not null) count.Text = tags.Length == 1 ? "1 tag" : $"{tags.Length} tags";
        if (tags.Length == 0)
        {
            ShowEmpty("IconTag", "No tags yet", "Labels that cut across projects, such as meeting, review or overtime.");
            return;
        }
        ShowTable("*", ["Tag"], tags.Select(tag =>
        {
            Control[] cells = [Ui.Row(8, Ui.Icon("IconTag", "secondary", "small").Centered(), Ui.Text(tag.Name, "medium").Centered())];
            MenuFlyout? menu = null;
            Func<Task>? open = null;
            if (owner.IsAdmin)
            {
                async Task Rename()
                {
                    if (await AskName("Rename tag", "e.g. meeting", "Save", tag.Name) is { } renamed)
                        await Change(() => owner.Api!.UpdateTagAsync(owner.WorkspaceId, tag.Id, new TagRequest { Name = renamed }), null, "Couldn't change the tag.");
                }
                menu = new MenuFlyout();
                menu.Items.Add(Item("Rename…", "IconEdit", Rename));
                menu.Items.Add(new Separator());
                menu.Items.Add(Item("Delete…", "IconDelete", async () =>
                {
                    if (await owner.ConfirmAsync($"Delete the tag “{tag.Name}”?", "It is removed from every time entry that has it. The time itself stays.", "Delete tag", destructive: true))
                        await Change(() => owner.Api!.DeleteTagAsync(owner.WorkspaceId, tag.Id), $"“{tag.Name}” deleted", "Couldn't delete the tag.");
                }));
                open = Rename;
            }
            return (cells, menu, open);
        }));
    }
}
