using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Avalonia.Input;
using Avalonia.Layout;
using Spindle.Core;
using Spindle.Windows.Model;

namespace Spindle.Windows.Views.Shell;

/// <summary>
/// The signed-in window, laid out as Windows 11 apps are: a title bar, a navigation pane on the window's backdrop
/// (Mica on Windows 11), and the section on a raised layer. The running timer is visible from every section.
/// </summary>
public sealed class ShellView : UserControl
{
    public const double TitleBarHeight = 44;

    private static readonly (string Heading, (Section Section, string Title, string Icon)[] Items)[] Groups =
    [
        ("Track", [(Section.Timer, "Timer", "IconTimer"), (Section.Calendar, "Calendar", "IconCalendarLtr")]),
        ("Analyze", [(Section.Reports, "Reports", "IconDataBarVertical")]),
        ("Manage", [(Section.Projects, "Projects", "IconFolder"), (Section.Clients, "Clients", "IconBuildingMultiple"), (Section.Tags, "Tags", "IconTag")]),
    ];

    private readonly MainWindow owner;
    private readonly Dictionary<Section, Button> navigation = [];
    private readonly ContentControl page = new();
    private readonly ContentControl banner = new();
    private readonly ContentControl sidebarTimer = new();
    private readonly ContentControl syncStatus = new();
    private Control? current;

    public ShellView(MainWindow owner)
    {
        this.owner = owner;
        var pane = Pane();
        var layer = new Border
        {
            Child = new DockPanel { Children = { banner.Dock(Dock.Top), page } },
            ClipToBounds = true,
        }.Classed("layer");
        var body = Ui.Columns("260,*", 0, pane, layer);
        var root = new Grid { RowDefinitions = new RowDefinitions($"{TitleBarHeight},*") };
        root.Children.Add(TitleBar());
        root.Children.Add(body.Row(1));
        Content = root;
        Refresh();
    }

    public Section Section { get; private set; } = Section.Timer;
    public TimerView? TimerView => current as TimerView;

    private Control TitleBar()
    {
        var title = Ui.Row(10, Ui.LogoMark(16).Centered(), Ui.Text("Spindle", "caption").Centered());
        title.Margin = new Thickness(16, 0, 0, 0);
        var right = OperatingSystem.IsWindows() ? 146 : 16;
        var bar = Ui.Columns("Auto,*,Auto", 12, title, new Border(), syncStatus.Centered().Margin(0, 0, right, 0));
        bar.Background = Avalonia.Media.Brushes.Transparent;
        WindowDecorationProperties.SetElementRole(bar, Avalonia.Input.WindowDecorationsElementRole.TitleBar);
        return bar;
    }

    private Control Pane()
    {
        var items = Ui.Stack(2);
        items.Children.Add(WorkspaceSwitcher().Margin(0, 0, 0, 10));
        foreach (var (heading, entries) in Groups)
        {
            items.Children.Add(Ui.Text(heading, "sectionHeader").Margin(12, 10, 0, 4));
            foreach (var (section, title, icon) in entries) items.Children.Add(NavItem(section, title, icon));
        }
        var settings = NavItem(Section.Settings, "Settings", "IconSettings");
        var footer = Ui.Stack(2, sidebarTimer, Ui.Divider().Margin(4, 6, 4, 6), settings, Account());
        var pane = new DockPanel { Margin = new Thickness(8, 4, 8, 8) };
        pane.Children.Add(footer.Dock(Dock.Bottom));
        pane.Children.Add(new ScrollViewer { Content = items });
        return pane;
    }

    private Button NavItem(Section section, string title, string icon)
    {
        var indicator = new Border().Classed("navIndicator").Centered();
        var content = Ui.Columns("3,Auto,*", 12, indicator, Ui.Icon(icon).Centered(), Ui.Text(title).Centered());
        content.Margin = new Thickness(-12, 0, 0, 0);
        var button = Ui.Button(content, () => NavigateAsync(section), owner.Notify, "nav").Named("Nav" + section).Spoken(title);
        navigation[section] = button;
        return button;
    }

    private Control WorkspaceSwitcher()
    {
        var name = Ui.Text(owner.Workspace?.Name ?? "", "medium");
        var label = Ui.Stack(0, Ui.Text("Workspace", "caption", "secondary"), name);
        var content = Ui.Columns("*,Auto", 8, label, Ui.Icon("IconChevronUpDown", "secondary", "small").Centered());
        var button = new Button { Content = content, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(12, 6) }
            .Classed("field").Named("WorkspaceSwitcher").Spoken("Workspace: " + name.Text);
        button.Res(Button.BackgroundProperty, "SubtleFillHoverBrush");
        var menu = new MenuFlyout();
        menu.Items.Add(new MenuItem { Header = "Your workspaces", IsEnabled = false });
        foreach (var workspace in owner.Me?.Workspaces ?? [])
        {
            var item = new MenuItem { Header = workspace.Name, Icon = workspace.Id == owner.WorkspaceId ? Ui.Icon("IconCheckmark") : null };
            item.Click += async (_, _) => await owner.SwitchWorkspaceAsync(workspace);
            menu.Items.Add(item);
        }
        if (owner.IsAdmin)
        {
            menu.Items.Add(new Separator());
            var create = new MenuItem { Header = "New workspace on the web…" };
            create.Click += (_, _) => owner.OpenWeb("settings");
            menu.Items.Add(create);
        }
        button.Flyout = menu;
        return button;
    }

    private Control Account()
    {
        var me = owner.Me;
        var content = Ui.Columns("Auto,*", 12, Ui.Avatar(me?.FullName ?? "", 24).Centered(), Ui.Text(me?.FullName ?? "").Centered());
        content.Margin = new Thickness(-4, 0, 0, 0);
        var button = new Button { Content = content }.Classed("nav").Named("Account").Spoken("Account");
        ToolTip.SetTip(button, me?.Email);
        var menu = new MenuFlyout { Placement = PlacementMode.TopEdgeAlignedLeft };
        menu.Items.Add(new MenuItem { Header = me?.FullName, IsEnabled = false });
        menu.Items.Add(new MenuItem { Header = me?.Email, IsEnabled = false });
        menu.Items.Add(new Separator());
        Add(menu, "Account settings", "IconPersonCircle", () => _ = owner.NavigateSafelyAsync(Section.Settings));
        if (owner.IsAdmin)
        {
            Add(menu, "Workspace settings on the web", "IconOpen", () => owner.OpenWeb("settings"));
            Add(menu, "Team on the web", "IconPeople", () => owner.OpenWeb("team"));
        }
        Add(menu, "Open Spindle on the web", "IconOpen", () => owner.OpenWeb("timer"));
        menu.Items.Add(new Separator());
        Add(menu, "Sign out", "IconSignOut", () => _ = owner.SignOutAsync());
        button.Flyout = menu;
        return button;
    }

    private static void Add(MenuFlyout menu, string header, string icon, Action action)
    {
        var item = new MenuItem { Header = header, Icon = Ui.Icon(icon) };
        item.Click += (_, _) => action();
        menu.Items.Add(item);
    }

    public async Task NavigateAsync(Section target)
    {
        if (owner.Api is not { } api || owner.Workspace is not { } workspace) return;
        Section = target;
        foreach (var (section, button) in navigation)
        {
            if (section == target) button.Classes.Add("selected");
            else button.Classes.Remove("selected");
        }
        current = target switch
        {
            Section.Timer => new TimerView(owner),
            Section.Calendar => new CalendarView(owner),
            Section.Reports => new ReportsView(owner),
            Section.Projects => new ProjectsView(owner),
            Section.Clients => new ClientsView(owner),
            Section.Tags => new TagsView(owner),
            _ => new SettingsView(owner),
        };
        page.Content = current;
        Refresh();
        if (current is ILoadable loadable) await loadable.LoadAsync();
    }

    /// <summary>Everything that follows the data: the page, the sidebar timer, the banner and the sync status.</summary>
    public void Refresh()
    {
        sidebarTimer.Content = Section != Section.Timer && owner.Running is { } running ? SidebarTimer(running) : null;
        var readOnly = owner.ReadOnlyOrganization is { } organization ? ReadOnlyBanner(organization) : null;
        var proposals = readOnly is null ? ProposalViews.Banner(owner) : null;
        banner.Content = readOnly ?? proposals;
        syncStatus.Content = SyncStatus();
        (current as ILivePage)?.Refresh();
    }

    public void Tick()
    {
        if (sidebarTimer.Content is Control card && card.Tag is TextBlock clock && owner.Running is { } running)
            clock.Text = Durations.Clock(running.Seconds(DateTimeOffset.UtcNow));
        (current as ILivePage)?.Tick();
    }

    /// <summary>The running timer at the foot of the pane, to see and stop it from any section.</summary>
    private Control SidebarTimer(TimeEntryResponse running)
    {
        var clock = Ui.Text(Durations.Clock(running.Seconds(DateTimeOffset.UtcNow)), "caption", "secondary", "mono");
        Control dot = running.IsSplit() ? SplitViews.Dots(owner.Catalog, running.Parts(), 7) : Ui.Dot(owner.Catalog.Project(running.ProjectId)?.Color, 7);
        var details = Ui.Stack(2,
            Ui.Text(running.Description.Length == 0 ? "No description" : running.Description, "medium", running.Description.Length == 0 ? "secondary" : ""),
            Ui.Row(6, dot, clock));
        var open = new Button { Content = details, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(4) }
            .Classed("subtle").Spoken("Open the running timer");
        open.Click += async (_, _) => await NavigateAsync(Section.Timer);
        var stop = Ui.Button(Ui.Icon("IconStopFilled", "small"), () => owner.StopAsync(), owner.Notify, "stop", "small").Tip("Stop timer").Spoken("Stop timer");
        var card = Ui.Card(Ui.Columns("*,Auto", 8, open, stop.Centered()), "flush");
        card.Padding = new Thickness(6, 6, 10, 6);
        card.Tag = clock;
        return card;
    }

    private Control ReadOnlyBanner(OrganizationSummary organization)
    {
        var reason = organization.Role == OrganizationRole.Member
            ? "Its trial or plan has ended, so time can't be added or changed. Everything is still here. Its owner can bring it back."
            : "Its trial or plan has ended. Everything is still here, and you can read, report and export it, but not add or change time.";
        var text = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        text.Inlines!.Add(new Avalonia.Controls.Documents.Run(organization.Name + " is read-only. ") { FontWeight = Avalonia.Media.FontWeight.SemiBold });
        text.Inlines.Add(new Avalonia.Controls.Documents.Run(reason));
        Control? action = organization.Role == OrganizationRole.Member ? null : Ui.Link("Choose a plan", () =>
        {
            owner.OpenWeb("plan");
            return Task.CompletedTask;
        }, owner.Notify).Centered();
        var row = Ui.Columns("Auto,*,Auto", 10, Ui.Icon("IconLockClosed", "critical").Centered(), text, action);
        return new Border { Child = row }.Classed("banner").Named("ReadOnlyBanner");
    }

    /// <summary>Nothing when all is well; otherwise that the app is offline or catching up, with what waits.</summary>
    private Control? SyncStatus()
    {
        var waiting = owner.PendingCount;
        if (owner.IsOffline)
        {
            return Ui.Row(6, Ui.Icon("IconCloudOff", "secondary", "small").Centered(), Ui.Text(waiting > 0 ? $"Offline · {waiting} waiting" : "Offline", "caption", "secondary"))
                .Tip(waiting > 0 ? "Spindle can't reach the server. Your changes are kept on this PC and sync when it's back." : "Spindle can't reach the server. You can go on tracking; changes sync when it's back.");
        }
        if (waiting > 0)
        {
            var link = Ui.Button(Ui.Row(6, Ui.Icon("IconArrowSync", "small").Centered(), Ui.Text($"{waiting} waiting to sync", "caption")), () => NavigateAsync(Section.Settings), owner.Notify, "subtle");
            link.Padding = new Thickness(8, 2);
            link.MinHeight = 24;
            return link.Tip("Open the changes waiting to sync");
        }
        return null;
    }
}

/// <summary>A page that loads its data once it is shown.</summary>
public interface ILoadable
{
    Task LoadAsync();
}
