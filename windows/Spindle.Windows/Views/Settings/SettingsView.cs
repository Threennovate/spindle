using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Spindle.Activity;
using Spindle.Core;
using Spindle.Platform;
using Spindle.Windows.Desktop;
using Spindle.Windows.Model;
using Spindle.Windows.Views.Shell;

namespace Spindle.Windows.Views;

/// <summary>
/// Settings as Windows 11 shows them, in cards with the control on the right: this PC's settings (General), the
/// account's Pomodoro, connecting agents (Agents), and the account itself. The Mac's tabs, in the same order.
/// </summary>
public sealed class SettingsView : UserControl, ILivePage
{
    private readonly MainWindow owner;
    private readonly ContentControl page = new();
    private readonly Segmented tabs;

    public SettingsView(MainWindow owner)
    {
        this.owner = owner;
        tabs = new Segmented([("General", "General"), ("Pomodoro", "Pomodoro"), ("Agents", "Agents"), ("Account", "Account")], owner.PendingCount > 0 ? 0 : 0, _ => Render());
        var top = Ui.Stack(14, Ui.Text("Settings", "title"), tabs);
        top.Margin = new Thickness(32, 20, 32, 12);
        page.Margin = new Thickness(32, 4, 32, 32);
        page.MaxWidth = 880;
        page.HorizontalAlignment = HorizontalAlignment.Left;
        Content = new DockPanel { Children = { top.Dock(Dock.Top), new ScrollViewer { Content = page } } };
        Render();
        AttachedToVisualTree += async (_, _) =>
        {
            owner.Updates.Changed += Refresh;
            // The account's repository links, for the list of them.
            if (Preferences.AgentLogsEnabled && owner.Workspace is not null)
            {
                await owner.Splits.LoadAsync([]);
                Refresh();
            }
        };
        DetachedFromVisualTree += (_, _) => owner.Updates.Changed -= Refresh;
    }

    internal void Show(int tab)
    {
        tabs.Select(tab);
        Render();
    }

    public void Refresh()
    {
        if (tabs.Selected == 0) Render();
    }

    public void Tick() { }

    private DesktopPreferences Preferences => owner.Preferences;

    private void Save(Func<DesktopPreferences, DesktopPreferences> change)
    {
        owner.SavePreferences(change(Preferences));
        Render();
    }

    private void Render() => page.Content = tabs.Selected switch
    {
        1 => new PomodoroSettings(owner),
        2 => new AgentSettings(owner),
        3 => new AccountSettings(owner),
        _ => General(),
    };

    // Cards

    /// <summary>A setting: an icon, its name and what it does, and its control on the right, as in Windows Settings.</summary>
    internal static Border Card(string? icon, string title, string? description, Control? control, Control? below = null)
    {
        var text = Ui.Stack(2, Ui.Text(title), description is null ? null : Ui.Wrapped(description, "caption", "secondary"));
        text.VerticalAlignment = VerticalAlignment.Center;
        var row = Ui.Columns("Auto,*,Auto", 16, icon is null ? new Border { Width = 0 } : Ui.Icon(icon, "large").Centered(), text, control?.Centered());
        var card = new Border { Child = below is null ? row : Ui.Stack(12, row, below.Margin(icon is null ? 0 : 36, 0, 0, 0)), Padding = new Thickness(16, 12), MinHeight = 64 }.Classed("card");
        card.Margin = new Thickness(0, 0, 0, 4);
        return card;
    }

    internal static Control Section(string title, params Control?[] cards)
    {
        var stack = Ui.Stack(0, Ui.Text(title, "strong").Margin(2, 20, 0, 8));
        foreach (var card in cards)
        {
            if (card is not null) stack.Children.Add(card);
        }
        return stack;
    }

    internal static Control Footer(string text) => Ui.Wrapped(text, "caption", "secondary").Margin(2, 4, 0, 0);

    internal static ToggleSwitch Switch(bool value, Action<bool> changed, string name)
    {
        var toggle = new ToggleSwitch { IsChecked = value, OnContent = "On", OffContent = "Off", Name = name };
        toggle.IsCheckedChanged += (_, _) => changed(toggle.IsChecked == true);
        return toggle;
    }

    internal static ComboBox Choice<T>(IReadOnlyList<(T Value, string Label)> choices, T selected, Action<T> changed, double width = 160)
    {
        var box = new ComboBox { ItemsSource = choices.Select(c => c.Label).ToArray(), Width = width };
        box.SelectedIndex = Math.Max(0, choices.ToList().FindIndex(c => Equals(c.Value, selected)));
        box.SelectionChanged += (_, _) =>
        {
            if (box.SelectedIndex >= 0 && !Equals(choices[box.SelectedIndex].Value, selected)) changed(choices[box.SelectedIndex].Value);
        };
        return box;
    }

    // General: this PC

    private Control General()
    {
        var windows = OperatingSystem.IsWindows();
        var stack = Ui.Stack(0);
        var login = Switch(Preferences.LaunchAtLogin, value => Save(p => p with { LaunchAtLogin = value }), "LaunchAtLogin");
        login.IsEnabled = windows;
        stack.Children.Add(Section("Start",
            Card("IconPower", "Open Spindle when you sign in to Windows", windows ? null : "Available on Windows.", login),
            Card("IconPin", "Keep Spindle in the notification area", "Closing the window keeps the timer, reminders and shortcuts running.",
                Switch(Preferences.MinimizeToTray, value => Save(p => p with { MinimizeToTray = value }), "MinimizeToTray"))));
        stack.Children.Add(Section("Keyboard shortcuts",
            Card("IconKeyboard", "Quick entry", "Opens a panel over any app: type what you work on, e.g. “Review PR @Acme since 9:40”.",
                Shortcut(Preferences.QuickEntryShortcut, value => Save(p => p with { QuickEntryShortcut = value }))),
            Card("IconPlayCircle", "Start or stop the timer", "Starts a timer without a description, or stops the running one.",
                Shortcut(Preferences.ToggleTimerShortcut, value => Save(p => p with { ToggleTimerShortcut = value }))),
            Card("IconAdd", "New entry", "Brings Spindle to the front, ready to type what you're working on.",
                Shortcut(Preferences.NewEntryShortcut, value => Save(p => p with { NewEntryShortcut = value }))),
            owner.ShortcutProblem is { } problem ? Ui.Wrapped(problem, "caption", "critical").Margin(2, 4, 0, 0) : null));
        var minutes = Choice([(3, "3 minutes"), (5, "5 minutes"), (10, "10 minutes"), (15, "15 minutes"), (30, "30 minutes")], Preferences.IdleMinutes, value => Save(p => p with { IdleMinutes = value }));
        minutes.IsEnabled = Preferences.DetectIdle;
        stack.Children.Add(Section("Time away",
            Card("IconClock", "Ask about time away from a running timer", null, Switch(Preferences.DetectIdle, value => Save(p => p with { DetectIdle = value }), "DetectIdle")),
            Card(null, "When away for", null, minutes),
            Footer("When you come back, keep the time, drop it, or drop it and go on with the same entry.")));
        stack.Children.Add(Section("Reminders",
            Card("IconAlert", "Remind me to track my time", "A notification when nothing is running during your working hours and you're at the PC.",
                Switch(Preferences.RemindToTrack, value => Save(p => p with { RemindToTrack = value }), "RemindToTrack"),
                Preferences.RemindToTrack ? ReminderSchedule() : null)));
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        // A checkbox per agent, with where its logs are.
        Control Source(AgentLogFormat format)
        {
            var path = Directory.Exists(format.LogsPath(home)) ? format.LogsPath(home) : "Not found";
            var box = new CheckBox { IsChecked = Preferences.Reads(format.Source), IsEnabled = Preferences.AgentLogsEnabled,
                Content = Ui.Stack(0, Ui.Text(format.Label), Ui.Text(path, "caption", "secondary")) };
            Avalonia.Automation.AutomationProperties.SetName(box, format.Label);
            box.IsCheckedChanged += (_, _) => Save(p => p.Reading(format.Source, box.IsChecked == true));
            return box;
        }
        stack.Children.Add(Section("Parallel work",
            Card("IconSparkle", "Suggest splits from agent activity", "When you worked on more than one project during an entry, offer to split its time.",
                Switch(Preferences.AgentLogsEnabled, value => Save(p => p with { AgentLogsEnabled = value, AgentLogsDecided = true }), "AgentLogs"),
                Ui.Stack(2, [.. AgentLogFormat.All.Select(Source)])),
            Card("IconArrowSplit", "Notify me when the work moves to another project", "While the timer runs on one project and your agents have been busy in another for a while.",
                Switch(Preferences.NotifySwitches, value => Save(p => p with { NotifySwitches = value }), "NotifySwitches")),
            Card("IconAlert", "Remind me to review the day", "Once, after your working hours, when the day has splits or untracked agent time waiting.", DayReviewSwitch()),
            Card("IconCalendarLtr", "Last week in the logs", "What the last 7 days of logs found in your tracked time, and the repositories to link to projects.", LogsFound()),
            LinkedRepositories(),
            Card("IconCalendarLtr", "Agent activity in the calendar", $"Your focus and agent runs from {AgentLogFormat.Names}, beside your entries.",
                Switch(Preferences.ShowAgentActivity, value => Save(p => p with { ShowAgentActivity = value }), "ShowAgentActivity")),
            owner.Me is null ? null : ActivitySyncCards(),
            Footer($"Reads {AgentLogFormat.Names} logs on this PC. Only times, folders, branches and session titles are kept, never prompts or answers. The logs stay on this PC unless you sync them; the projects you choose for repositories are kept with your account. Nothing is split until you say so.")));
        stack.Children.Add(Section("Changes waiting to sync", PendingChanges()));
        stack.Children.Add(Section("About", About()));
        return stack;
    }

    private ToggleSwitch DayReviewSwitch()
    {
        var toggle = Switch(Preferences.NotifyDayReview, value => Save(p => p with { NotifyDayReview = value }), "NotifyDayReview");
        toggle.IsEnabled = Preferences.AgentLogsEnabled && Preferences.NotifySwitches;
        return toggle;
    }

    private Button LogsFound()
    {
        var button = Ui.Button("Show what the logs found…", owner.ShowLogsFoundAsync, owner.Notify).Named("ShowLogsFound");
        button.IsEnabled = Preferences.AgentLogsEnabled && owner.Workspace is not null;
        return button;
    }

    /// <summary>The repositories linked to projects, each with its branch rules, which can be changed here.</summary>
    private Control? LinkedRepositories()
    {
        if (!Preferences.AgentLogsEnabled || owner.Workspace is null) return null;
        var linked = owner.Splits.Linked();
        var list = Ui.Stack(0);
        foreach (var link in linked)
        {
            if (list.Children.Count > 0) list.Children.Add(Ui.Divider());
            var name = Ui.Text(link.Repository.Name, "medium");
            name.TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis;
            var project = RepositoryLinkButton.Create(owner, link.Repository, link.ProjectId, Render);
            project.HorizontalAlignment = HorizontalAlignment.Left;
            var rules = link.Rules.Count switch { 0 => null, 1 => Ui.Text("1 branch rule", "caption", "secondary"), var count => Ui.Text($"{count} branch rules", "caption", "secondary") };
            var row = Ui.Columns("2*,Auto,3*,Auto,Auto", 10, name.Centered(), Ui.Text("→", "tertiary").Centered(), project.Centered(), rules?.Centered(),
                link.ProjectId is null ? null : BranchRulesButton.Create(owner, link.Repository, @short: true, saved: Render).Centered());
            row.Margin = new Thickness(0, 6);
            row.Background = Avalonia.Media.Brushes.Transparent;
            if (link.ProjectId is not null)
            {
                var unlink = new MenuItem { Header = "Unlink", Icon = Ui.Icon("IconDismiss") }.Named("UnlinkRepository");
                var repository = link.Repository;
                unlink.Click += async (_, _) =>
                {
                    await RepositoryLinkButton.UnlinkAsync(owner, repository);
                    Render();
                };
                row.ContextFlyout = new MenuFlyout { Items = { unlink } };
            }
            list.Children.Add(row.Named("LinkedRepository"));
        }
        if (linked.Count == 0) list.Children.Add(Ui.Wrapped("None yet. Choose a project for a repository in a suggestion, or in “Show what the logs found…”.", "caption", "secondary"));
        return Card("IconLink", "Linked repositories", "Where each repository's agent work goes; No project unlinks it. Branch rules send some branches to another project, e.g. a client's.", null, list);
    }

    /// <summary>Uploading agent activity to the account, so the web and the server's MCP see it too. An account setting, on every device.</summary>
    private Control ActivitySyncCards()
    {
        var sync = owner.ActivitySync;
        async void Change(bool on, bool titles)
        {
            try
            {
                await sync.ChangeAsync(on, titles);
            }
            catch (Exception error)
            {
                owner.Notify(error.Message);
            }
            Render();
        }
        var enabled = Preferences.AgentLogsEnabled;
        var syncing = Switch(sync.IsSyncing, on => Change(on, false), "AgentActivitySync");
        // Turning sync on needs the logs read here; turning it off, which deletes what was sent, never does.
        syncing.IsEnabled = (enabled || sync.IsSyncing) && !sync.IsChanging;
        var titles = Switch(sync.IncludesTitles, on => Change(true, on), "AgentActivityTitles");
        titles.IsEnabled = sync.IsSyncing && (enabled || sync.IncludesTitles) && !sync.IsChanging;
        return Ui.Stack(0,
            Card("IconArrowSync", "Sync agent activity to your account", "So the web and your AI assistants (through MCP) see it too. Sends when you sent prompts, when agents were busy, and each repository's name, git origin and branches; never folder paths, prompts or answers. On all your devices. Turning it off deletes what was sent.", syncing),
            Card(null, "Include session titles", "A session's title can be the first line of a prompt. Splits work without them.", titles),
            !enabled && sync.IsSyncing ? Ui.Wrapped("Reading agent logs is off on this device, so it sends nothing new. Turn sync off to delete what was sent.", "caption", "secondary").Margin(2, 4, 0, 0) : null,
            sync.IsSyncing && sync.Problem is { } problem ? Ui.Wrapped(problem, "caption", "critical").Margin(2, 4, 0, 0) : null);
    }

    private Control About()
    {
        var updates = owner.Updates;
        var version = $"Version {AppUpdates.Version}.";
        if (!updates.IsInstalled) return Card("IconInfoFilled", "Spindle for Windows", $"{version} This build doesn't update itself.", null);
        if (updates.Ready is { } ready)
        {
            var restart = Ui.Button("Restart to update", () =>
            {
                updates.RestartAfterQuit();
                owner.Quit();
            }, "primary");
            return Card("IconArrowSync", "Spindle for Windows", $"{version} Spindle {ready} is ready and installs when Spindle restarts or quits.", restart);
        }
        var status = updates.IsChecking ? "Checking for updates…"
            : updates.LastError is { } error ? $"Couldn't check for updates: {error}"
            : updates.LastChecked is not null ? "Spindle is up to date."
            : "New versions are installed automatically.";
        var check = Ui.Button("Check for updates", updates.CheckAsync, owner.Notify);
        check.IsEnabled = !updates.IsChecking;
        return Card("IconInfoFilled", "Spindle for Windows", $"{version} {status}", check);
    }

    private Control ReminderSchedule()
    {
        var days = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        foreach (var (day, letter) in new[] { (1, "M"), (2, "T"), (3, "W"), (4, "T"), (5, "F"), (6, "S"), (0, "S") })
        {
            var on = Preferences.RemindsOn(day);
            var toggle = new Button { Content = letter, Width = 32, Height = 32, Padding = new Thickness(0), CornerRadius = new CornerRadius(16) }.Classed(on ? "primary" : "")
                .Spoken(System.Globalization.CultureInfo.GetCultureInfo("en-GB").DateTimeFormat.DayNames[day]);
            toggle.Click += (_, _) => Save(p => p with { ReminderDayMask = p.ReminderDayMask ^ (1 << day) });
            days.Children.Add(toggle);
        }
        var times = Enumerable.Range(0, 49).Select(i => (i * 30, i == 48 ? "24:00 (midnight)" : new ClockTime(i / 2, i % 2 * 30).Field(owner.Time.Hour12))).ToArray();
        var from = Choice(times, Preferences.ReminderFrom, value => Save(p => p with { ReminderFrom = value }), 130);
        var to = Choice(times, Preferences.ReminderTo, value => Save(p => p with { ReminderTo = value }), 150);
        var every = Choice([(15, "15 minutes"), (30, "30 minutes"), (60, "hour"), (120, "2 hours")], Preferences.ReminderEveryMinutes, value => Save(p => p with { ReminderEveryMinutes = value }), 130);
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto"), ColumnSpacing = 12, RowSpacing = 8 };
        grid.Children.Add(Ui.Text("On", "secondary").Centered());
        grid.Children.Add(days.Column(1));
        grid.Children.Add(Ui.Text("From", "secondary").Centered().Row(1));
        grid.Children.Add(Ui.Row(8, from, Ui.Text("to").Centered(), to).Column(1).Row(1));
        grid.Children.Add(Ui.Text("Every", "secondary").Centered().Row(2));
        grid.Children.Add(every.Column(1).Row(2));
        return grid;
    }

    /// <summary>A keyboard shortcut: click, then press the keys; the cross removes it.</summary>
    private Control Shortcut(string? value, Action<string?> changed)
    {
        var recorder = new Button { Content = value ?? "Record shortcut", MinWidth = 150 }.Classed("mono");
        recorder.Click += (_, _) =>
        {
            recorder.Content = "Press the keys…";
            recorder.Focus();
        };
        recorder.KeyDown += (_, e) =>
        {
            if (!Equals(recorder.Content, "Press the keys…") && !Equals(recorder.Content, "Add Ctrl, Alt or Win")) return;
            e.Handled = true;
            if (e.Key == Key.Escape)
            {
                recorder.Content = value ?? "Record shortcut";
                return;
            }
            if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;
            if ((e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta)) == 0)
            {
                recorder.Content = "Add Ctrl, Alt or Win";
                return;
            }
            changed(HotKeyText.Format(e.KeyModifiers, e.Key));
        };
        var clear = Ui.IconButton("IconDismiss16", "Remove the shortcut", () => changed(null), "small");
        clear.IsVisible = value is not null;
        return Ui.Row(4, recorder, clear);
    }

    private Control PendingChanges()
    {
        var pending = owner.Tracker?.PendingChanges ?? [];
        if (pending.Count == 0) return Card("IconCheckmarkCircleFilled", "Everything is synced", owner.IsOffline ? "Spindle can't reach the server right now." : "Changes you make reach the server at once.", null);
        var list = Ui.Stack(4);
        foreach (var change in pending)
        {
            var what = change.Desired?.Description ?? change.Baseline?.Description;
            var title = $"{change.Kind}: {(string.IsNullOrEmpty(what) ? "No description" : what)}";
            var retry = Ui.Button("Retry", async () =>
            {
                await owner.Tracker!.RetryPendingAsync(change.Id);
                await owner.RefreshSafelyAsync();
                Render();
            }, owner.Notify);
            var discard = Ui.Button("Discard…", async () =>
            {
                if (!await owner.ConfirmAsync("Discard this change?", "This restores the server's entry and removes dependent edits on this PC.", "Discard", destructive: true)) return;
                await owner.Tracker!.DiscardPendingAsync(change.Id);
                await owner.RefreshSafelyAsync();
                Render();
            }, owner.Notify, "danger");
            list.Children.Add(Card(change.Error is null ? "IconArrowSync" : "IconWarningFilled", title, change.Error ?? "Waiting for the connection.", Ui.Row(8, retry, discard)));
        }
        return list;
    }
}

/// <summary>Writes and reads keyboard shortcuts as Windows shows them, e.g. Ctrl+Alt+Space.</summary>
public static class HotKeyText
{
    public static string Format(KeyModifiers modifiers, Key key)
    {
        var parts = new List<string>();
        if (modifiers.HasFlag(KeyModifiers.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(KeyModifiers.Meta)) parts.Add("Win");
        parts.Add(key switch
        {
            >= Key.D0 and <= Key.D9 => ((int)(key - Key.D0)).ToString(),
            _ => key.ToString(),
        });
        return string.Join("+", parts);
    }

    /// <summary>The Win32 modifiers and virtual-key code of a shortcut, or null when it can't be registered.</summary>
    public static (uint Modifiers, uint Key)? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        uint modifiers = 0;
        uint? key = null;
        foreach (var part in text.Split('+'))
        {
            switch (part)
            {
                case "Ctrl": modifiers |= 0x0002; break;
                case "Alt": modifiers |= 0x0001; break;
                case "Shift": modifiers |= 0x0004; break;
                case "Win": modifiers |= 0x0008; break;
                case "Space": key = 0x20; break;
                case var letter when letter.Length == 1 && char.IsAsciiLetterUpper(letter[0]): key = letter[0]; break;
                case var digit when digit.Length == 1 && char.IsAsciiDigit(digit[0]): key = digit[0]; break;
                case var function when function.StartsWith('F') && int.TryParse(function[1..], out var number) && number is >= 1 and <= 24: key = (uint)(0x6F + number); break;
                default: return null;
            }
        }
        return key is { } code && (modifiers & 0x000B) != 0 ? (modifiers, code) : null;
    }
}
