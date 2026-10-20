using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using Spindle.Core;
using Spindle.Windows.Model;

namespace Spindle.Windows.Views;

/// <summary>
/// The panel the notification-area icon opens, as the Mac's menu bar timer: the running timer and its stop button, or
/// a field to start one, recent entries to continue, quick entry, and opening or quitting Spindle.
/// </summary>
public sealed class TrayFlyout : Window
{
    private readonly MainWindow owner;
    private readonly Action quickEntry;
    private readonly Action quit;
    private readonly ContentControl body = new();
    private readonly DispatcherTimer ticking = new() { Interval = TimeSpan.FromSeconds(1) };
    private TextBlock? clock;

    public TrayFlyout(MainWindow owner, Action quickEntry, Action quit)
    {
        this.owner = owner;
        this.quickEntry = quickEntry;
        this.quit = quit;
        Width = 340;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        Topmost = true;
        WindowDecorations = WindowDecorations.None;
        Title = "Spindle";
        this.Res(BackgroundProperty, "FlyoutBackgroundBrush");
        Content = new Border { Child = body, BorderThickness = new Thickness(1), Padding = new Thickness(14) }.Res(Border.BorderBrushProperty, "FlyoutStrokeBrush");
        Deactivated += (_, _) => Hide();
        KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            Hide();
            e.Handled = true;
        };
        ticking.Tick += (_, _) =>
        {
            if (clock is not null && owner.Running is { } running) clock.Text = Durations.Clock(running.Seconds(DateTimeOffset.UtcNow));
        };
        Opened += (_, _) => Place();
        owner.TimerChanged += () =>
        {
            if (IsVisible && (owner.Running is null) != (clock is null)) Render();
        };
    }

    /// <summary>Shows the panel above the notification area, or hides it when it is open.</summary>
    public void Toggle()
    {
        if (IsVisible)
        {
            Hide();
            return;
        }
        Render();
        Show();
        Place();
        Activate();
        ticking.Start();
    }

    public new void Hide()
    {
        ticking.Stop();
        base.Hide();
    }

    private void Place()
    {
        if (Screens.Primary is not { } screen) return;
        var area = screen.WorkingArea;
        var scale = screen.Scaling;
        Position = new PixelPoint(area.Right - (int)((Width + 12) * scale), area.Bottom - (int)((Bounds.Height + 12) * scale));
    }

    public void Render()
    {
        clock = null;
        var stack = Ui.Stack(12);
        if (owner.Me is null)
        {
            stack.Children.Add(Ui.Wrapped("Sign in to start tracking your time.", "secondary"));
        }
        else if (owner.Running is { } running)
        {
            stack.Children.Add(RunningCard(running));
            if (owner.Splits.Suggestion(running) is { } suggestion) stack.Children.Add(OfferCard(running, suggestion));
            else if (owner.Splits.Drift(running) is { } drift) stack.Children.Add(DriftCard(drift));
        }
        else
        {
            stack.Children.Add(StartRow());
        }
        if (owner.Me is not null && owner.AgentLogsEnabled && owner.Desktop.Review([owner.Time.Today()]) is { Count: > 0 } toReview)
        {
            var today = owner.Time.Today();
            stack.Children.Add(Row(Ui.Columns("Auto,*,Auto", 10, Ui.Icon("IconSparkle", "brand").Centered(), Ui.Text($"Today: {toReview.Count} to review", "strong").Centered(),
                Ui.Icon("IconChevronRight", "secondary", "small").Centered()), () =>
            {
                Hide();
                return owner.OpenReviewAsync(ReviewScope.Day(today));
            }).Tip("Splits and agent time nobody tracked today, in one place").Named("TrayReview"));
        }
        if (owner.Me is not null) stack.Children.Add(Recent());
        stack.Children.Add(Ui.Divider());
        var shortcut = owner.Preferences.QuickEntryShortcut;
        var quick = Row(Ui.Columns("Auto,*,Auto", 10, Ui.Icon("IconSearch").Centered(), Ui.Text("Quick entry…").Centered(), shortcut is null ? null : Ui.Text(shortcut, "caption", "secondary").Centered()), () =>
        {
            Hide();
            quickEntry();
        }).Tip("Type what you work on from any app").Named("TrayQuickEntry");
        var open = Ui.Button(Ui.Labeled("IconWindow", "Open Spindle"), () =>
        {
            Hide();
            owner.Show();
            owner.WindowState = WindowState.Normal;
            owner.Activate();
        }, "subtle").Named("TrayOpen");
        var exit = Ui.Button(Ui.Labeled("IconPower", "Quit"), () =>
        {
            Hide();
            quit();
        }, "subtle").Named("TrayQuit");
        stack.Children.Add(quick);
        stack.Children.Add(Ui.Columns("*,Auto", 8, open, exit));
        open.HorizontalAlignment = HorizontalAlignment.Left;
        body.Content = stack;
    }

    private Control RunningCard(TimeEntryResponse running)
    {
        var catalog = owner.Catalog;
        var description = Ui.Wrapped(running.Description.Length == 0 ? "No description" : running.Description, "bodyLarge", running.Description.Length == 0 ? "secondary" : "");
        description.MaxLines = 2;
        Control project = running.IsSplit() ? SplitViews.Label(catalog, running.Parts()) : SplitViews.ProjectOrNone(catalog, running.ProjectId, running.TaskId);
        clock = Ui.Text(Durations.Clock(running.Seconds(DateTimeOffset.UtcNow)), "mono").Named("TrayClock");
        clock.FontSize = 22;
        clock.FontWeight = Avalonia.Media.FontWeight.SemiBold;
        var stop = Ui.Button(Ui.Icon("IconStopFilled"), async () =>
        {
            await owner.StopAsync();
            Render();
        }, owner.Notify, "stop").Tip("Stop timer").Spoken("Stop timer").Named("TrayStop");
        stop.Width = 38;
        stop.Height = 38;
        stop.CornerRadius = new CornerRadius(19);
        return Ui.Card(Ui.Stack(8, description, project, Ui.Columns("*,Auto", 8, clock.Centered(), stop)));
    }

    private Control OfferCard(TimeEntryResponse running, Spindle.Activity.SplitSuggestion suggestion)
    {
        var offer = new Desktop.SplitOffer(running, suggestion);
        var keep = Ui.Button("Keep as is", () => owner.Desktop.KeepAsync(offer), owner.Notify).Tip("Leave the timer on one project and stop suggesting a split for it");
        // Two stretches on the running entry switch the timer to where the work moved.
        var title = suggestion.Stretches is { Count: 2 } stretches
            ? $"Switch at {owner.Time.Clock(DateTimeOffset.FromUnixTimeMilliseconds(stretches[1].Start))}"
            : owner.Splits.Title(suggestion);
        var take = Ui.Button(title, async () =>
        {
            await owner.Desktop.TakeAsync(offer);
            Render();
        }, owner.Notify, "primary");
        return Ui.Card(Ui.Stack(8,
            Ui.Row(6, Ui.Icon("IconSparkle", "brand").Centered(), Ui.Text("Agents worked on more than one project", "strong")),
            Ui.Wrapped(owner.Splits.Summary(suggestion, running), "secondary"),
            Ui.Row(8, keep, take)));
    }

    /// <summary>The running split's work changed after it was set: end it there and carry on, from here.</summary>
    private Control DriftCard(Model.DriftOffer offer)
    {
        var notNow = Ui.Button("Not now", async () =>
        {
            await owner.Desktop.NotNowAsync(offer);
            Render();
        }, owner.Notify).Tip("Leave the timer as it is; nothing more is offered until its split changes");
        var take = Ui.Button(owner.Splits.Title(offer.Drift), async () =>
        {
            await owner.Desktop.TakeAsync(offer);
            Render();
        }, owner.Notify, "primary").Named("TrayTakeDrift");
        return Ui.Card(Ui.Stack(8,
            Ui.Row(6, Ui.Icon("IconSparkle", "brand").Centered(), Ui.Text(owner.Splits.Headline(offer.Drift), "strong")),
            Ui.Wrapped(owner.Splits.Question(offer), "secondary"),
            Ui.Row(8, notNow, take)));
    }

    private Control StartRow()
    {
        var field = new TextBox { PlaceholderText = "What are you working on?", Name = "TrayDescription" };
        async Task Start()
        {
            await owner.StartAsync(new CreateTimeEntryRequest { Description = (field.Text ?? "").Trim() });
            field.Text = "";
            Render();
        }
        field.KeyDown += async (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            try
            {
                await Start();
            }
            catch (Exception exception)
            {
                owner.Notify(exception.Message);
            }
        };
        var play = Ui.Button(Ui.Icon("IconPlayFilled"), Start, owner.Notify, "start").Tip("Start timer").Spoken("Start timer").Named("TrayStart");
        return Ui.Columns("*,Auto", 8, field.Centered(), play);
    }

    /// <summary>Up to five recent entries, each different, to continue with a click.</summary>
    private Control Recent()
    {
        var recent = owner.Entries
            .Where(e => e.Stop is not null)
            .OrderByDescending(e => e.Start)
            .DistinctBy(e => (e.Description, e.ProjectId, e.TaskId, string.Join(",", e.TagIds), string.Join(",", e.Shares.Select(s => $"{s.ProjectId}{s.Percent}"))))
            .Take(5)
            .ToArray();
        var list = Ui.Stack(0, Ui.Text("Recent", "sectionHeader").Margin(4, 0, 0, 4));
        if (recent.Length == 0) list.Children.Add(Ui.Text("Nothing tracked yet.", "secondary").Margin(4, 0, 0, 0));
        foreach (var entry in recent)
        {
            var catalog = owner.Catalog;
            Control project = entry.IsSplit() ? SplitViews.Label(catalog, entry.Parts()) : SplitViews.ProjectOrNone(catalog, entry.ProjectId, entry.TaskId);
            var text = Ui.Stack(1, Ui.Text(entry.Description.Length == 0 ? "No description" : entry.Description, entry.Description.Length == 0 ? "secondary" : ""), project);
            list.Children.Add(Row(Ui.Columns("Auto,*", 10, Ui.Icon("IconPlayFilled", "brand", "small").Centered(), text), async () =>
            {
                await owner.ContinueAsync(entry);
                Render();
            }).Tip("Continue"));
        }
        return list;
    }

    private Button Row(Control content, Func<Task> action)
    {
        var button = new Button { Content = content, Padding = new Thickness(6, 6) }.Classed("row");
        button.Click += async (_, _) =>
        {
            try
            {
                await action();
            }
            catch (Exception exception)
            {
                owner.Notify(exception.Message.Length > 0 ? exception.Message : "Couldn't start the timer.");
            }
        };
        return button;
    }

    private Button Row(Control content, Action action) => Row(content, () =>
    {
        action();
        return Task.CompletedTask;
    });
}
