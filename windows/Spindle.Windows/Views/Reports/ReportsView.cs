using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Spindle.Core;
using Spindle.Windows.Model;
using Spindle.Windows.Views.Shell;

namespace Spindle.Windows.Views;

/// <summary>
/// Summary, detailed and weekly reports of a period, filtered by member, project, client, tag, billable and
/// description, with rounding, as on the Mac. Every figure is the server's, which divides split entries by their parts.
/// </summary>
public sealed class ReportsView : UserControl, ILoadable
{
    private static readonly Guid None = Guid.Empty;
    private readonly MainWindow owner;
    private readonly Segmented tabs;
    private readonly Button period;
    private readonly WrapPanel filters = new() { Orientation = Orientation.Horizontal };
    private readonly StackPanel content = new() { Spacing = 16, Margin = new Thickness(32, 16, 32, 32) };
    private readonly TextBox description = new() { PlaceholderText = "Description", Width = 170, MaxLength = 200 };
    private readonly DispatcherTimer debounce = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private MemberResponse[] members = [];
    private GroupResponse[] groups = [];
    private int generation;

    public ReportsView(MainWindow owner)
    {
        this.owner = owner;
        tabs = new Segmented([("Summary", "Summary"), ("Detailed", "Detailed"), ("Weekly", "Weekly")], 0, index =>
        {
            Kind = (ReportKind)index;
            Page = 1;
            Changed();
        });
        period = new Button().Named("ReportPeriod");
        period.Click += (_, _) => PeriodMenu().ShowAt(period);
        var previous = Ui.IconButton("IconChevronLeft", "Previous period", () => Shift(-1));
        var next = Ui.IconButton("IconChevronRight", "Next period", () => Shift(1));
        var export = new Button { Content = Ui.Labeled("IconArrowDownload", "Export"), Flyout = ExportMenu() }.Spoken("Export");
        var header = Ui.Columns("Auto,Auto,*,Auto", 12, tabs, Ui.Row(4, previous, period, next), null, export);
        description.TextChanged += (_, _) => Changed();
        debounce.Tick += async (_, _) =>
        {
            debounce.Stop();
            await RunAsync();
        };
        var title = Ui.Stack(2, Ui.Text("Reports", "title"), Ui.Text((owner.IsAdmin ? "Time tracked in " : "Your time in ") + owner.Workspace?.Name, "secondary"));
        var top = Ui.Stack(14, title, header, filters);
        top.Margin = new Thickness(32, 20, 32, 12);
        Content = new DockPanel { Children = { top.Dock(Dock.Top), Ui.Divider().Dock(Dock.Top), new ScrollViewer { Content = content } } };
        RenderHeader();
    }

    // Settings of the report, as the Mac keeps them

    internal ReportKind Kind { get; private set; } = ReportKind.Summary;
    internal ReportPeriod Period { get; private set; } = ReportPeriod.ThisWeek;
    internal DayRange? Custom { get; private set; }
    internal HashSet<Guid> Members { get; } = [];
    internal HashSet<Guid> Projects { get; } = [];
    internal HashSet<Guid> Clients { get; } = [];
    internal HashSet<Guid> Tags { get; } = [];
    internal bool? Billable { get; set; }
    internal RoundingMode Rounding { get; private set; } = RoundingMode.None;
    internal int RoundingMinutes { get; private set; } = 15;
    internal ReportGrouping GroupBy { get; private set; } = ReportGrouping.Project;
    internal ReportGrouping? SubGroupBy { get; private set; } = ReportGrouping.Description;
    internal ReportGrouping WeeklyGroupBy { get; private set; } = ReportGrouping.Project;
    internal ReportSort Sort { get; private set; } = ReportSort.Date;
    internal bool Descending { get; private set; } = true;
    internal int Page { get; private set; } = 1;
    private readonly HashSet<string> expanded = [];

    internal DayRange Range
    {
        get
        {
            var today = owner.Time.Today();
            var range = DayRange.Resolve(Period, Custom, today, owner.WeekStart);
            if (Kind != ReportKind.Weekly) return range;
            return Period == ReportPeriod.Custom ? DayRange.Week(range.From, owner.WeekStart) : DayRange.Week(range.To < today ? range.To : today, owner.WeekStart);
        }
    }

    private bool Filtered => Members.Count + Projects.Count + Clients.Count + Tags.Count > 0 || Billable is not null || (description.Text ?? "").Trim().Length > 0;

    public async Task LoadAsync()
    {
        if (owner.IsAdmin && owner.Api is { } api)
        {
            try
            {
                var memberTask = api.GetAsync<MemberResponse[]>($"/api/workspaces/{owner.WorkspaceId}/members");
                var groupTask = api.GetAsync<GroupResponse[]>($"/api/workspaces/{owner.WorkspaceId}/groups");
                members = await memberTask;
                groups = await groupTask;
            }
            catch (Exception)
            {
                // Reports still work without the member filter.
            }
        }
        RenderHeader();
        await RunAsync();
    }

    /// <summary>Any change reloads after a moment, so typing and quick clicks run one report.</summary>
    internal void Changed()
    {
        Page = Page < 1 ? 1 : Page;
        RenderHeader();
        debounce.Stop();
        debounce.Start();
    }

    private void Shift(int direction)
    {
        Custom = Range.Shifted(direction);
        Period = ReportPeriod.Custom;
        Page = 1;
        Changed();
    }

    // Header and filters

    private void RenderHeader()
    {
        period.Content = Ui.Labeled("IconCalendarLtr", Range.Text());
        period.Spoken("Period: " + Range.Text());
        filters.Children.Clear();
        Control Gap(Control control) => control.Margin(0, 0, 4, 4);
        if (owner.IsAdmin) filters.Children.Add(Gap(FilterButton("Member", Members.Count, MemberRows)));
        filters.Children.Add(Gap(FilterButton("Project", Projects.Count, ProjectRows)));
        filters.Children.Add(Gap(FilterButton("Client", Clients.Count, ClientRows)));
        filters.Children.Add(Gap(FilterButton("Tag", Tags.Count, TagRows)));
        filters.Children.Add(Gap(BillableButton()));
        if (description.Parent is Panel old) old.Children.Remove(description);
        filters.Children.Add(Gap(description.Centered()));
        if (Filtered)
            filters.Children.Add(Gap(Ui.Button("Clear filters", () =>
            {
                Members.Clear();
                Projects.Clear();
                Clients.Clear();
                Tags.Clear();
                Billable = null;
                description.Text = "";
                Changed();
            }, "link").Centered()));
        filters.Children.Add(Gap(RoundingButton()));
    }

    private Button FilterButton(string label, int count, Func<string, IReadOnlyList<PickerRow>> rows)
    {
        var text = Ui.Text(label, count > 0 ? "brand" : "", "medium").Centered();
        var row = Ui.Row(6, text);
        if (count > 0) row.Children.Add(new Border { Child = Ui.Text(count.ToString(), "caption", "strong", "onBrand") }.Classed("badge"));
        row.Children.Add(Ui.Icon("IconChevronDown", "secondary", "small").Centered());
        var button = new Button { Content = row }.Classed("field").Spoken(label).Named("Filter" + label);
        button.Click += (_, _) =>
        {
            PickerList? picker = null;
            picker = new PickerList("Search", term => rows(term), _ => "Nothing matches.", 300, 340);
            PickerList.Show(button, picker).Closed += (_, _) => RenderHeader();
        };
        return button;
    }

    /// <summary>A row that ticks an ID in a filter on and off; the list stays open to pick more.</summary>
    private PickerRow Check(HashSet<Guid> set, Guid id, string label, string? detail = null, string? color = null)
    {
        var box = new Border { Width = 16, Height = 16, CornerRadius = new CornerRadius(3), VerticalAlignment = VerticalAlignment.Center };
        if (set.Contains(id))
        {
            box.Res(Border.BackgroundProperty, "AccentBrush");
            box.Child = Ui.Icon("IconCheckmark", "small").Res(PathIcon.ForegroundProperty, "OnCriticalBrush");
        }
        else
        {
            box.BorderThickness = new Thickness(1.2);
            box.Res(Border.BorderBrushProperty, "ControlStrongStrokeBrush");
        }
        var row = Ui.Columns("Auto,Auto,*,Auto", 8, box, color is null ? null : Ui.Dot(color, 8), Ui.Text(label).Centered(), detail is null ? null : Ui.Text(detail, "caption", "secondary").Centered());
        return new PickerRow(id.ToString(), row, false, () =>
        {
            if (!set.Remove(id)) set.Add(id);
            Page = 1;
            Changed();
            return Task.CompletedTask;
        });
    }

    private static bool Matches(string term, params string?[] words) => term.Length == 0 || words.Any(w => w?.Contains(term, StringComparison.CurrentCultureIgnoreCase) == true);

    private IReadOnlyList<PickerRow> MemberRows(string term)
    {
        var rows = new List<PickerRow>();
        var shownGroups = groups.Where(g => Matches(term, g.Name)).ToArray();
        if (shownGroups.Length > 0) rows.Add(PickerRow.Header("groups", "Groups"));
        rows.AddRange(shownGroups.Select(g => Check(Members, g.Id, g.Name, g.MemberIds.Length == 1 ? "1 member" : $"{g.MemberIds.Length} members")));
        var shownMembers = members.Where(m => Matches(term, m.FullName, m.Email)).OrderBy(m => m.FullName).ToArray();
        if (shownMembers.Length > 0) rows.Add(PickerRow.Header("members", "Members"));
        rows.AddRange(shownMembers.Select(m => Check(Members, m.UserId, m.FullName)));
        return rows;
    }

    private IReadOnlyList<PickerRow> ProjectRows(string term)
    {
        var rows = new List<PickerRow>();
        if (Matches(term, "No project")) rows.Add(Check(Projects, None, "No project"));
        var catalog = owner.Catalog;
        foreach (var group in catalog.ProjectGroups())
        {
            var shown = group.Projects.Where(p => Matches(term, p.Name, group.Client?.Name)).ToArray();
            if (shown.Length == 0) continue;
            rows.Add(PickerRow.Header("client:" + group.Client?.Id, group.Client?.Name ?? "No client"));
            rows.AddRange(shown.Select(p => Check(Projects, p.Id, p.Name, color: p.Color)));
        }
        var archived = catalog.Projects.Where(p => p.ArchivedAt is not null && Matches(term, p.Name)).OrderBy(p => p.Name).ToArray();
        if (archived.Length > 0) rows.Add(PickerRow.Header("archived", "Archived"));
        rows.AddRange(archived.Select(p => Check(Projects, p.Id, p.Name, "archived", p.Color)));
        return rows;
    }

    private IReadOnlyList<PickerRow> ClientRows(string term)
    {
        var rows = new List<PickerRow>();
        if (Matches(term, "No client")) rows.Add(Check(Clients, None, "No client"));
        var shown = owner.Catalog.Clients.Where(c => Matches(term, c.Name)).OrderBy(c => c.Name).ToArray();
        if (shown.Length > 0) rows.Add(PickerRow.Header("clients", "Clients"));
        rows.AddRange(shown.Select(c => Check(Clients, c.Id, c.Name, c.ArchivedAt is null ? null : "archived")));
        return rows;
    }

    private IReadOnlyList<PickerRow> TagRows(string term)
    {
        var rows = new List<PickerRow>();
        if (Matches(term, "No tag")) rows.Add(Check(Tags, None, "No tag"));
        var shown = owner.Catalog.SortedTags.Where(t => Matches(term, t.Name)).ToArray();
        if (shown.Length > 0) rows.Add(PickerRow.Header("tags", "Tags"));
        rows.AddRange(shown.Select(t => Check(Tags, t.Id, t.Name)));
        return rows;
    }

    private Button BillableButton()
    {
        var label = Billable == false ? "Non-billable" : "Billable";
        var content = Ui.Row(6, Ui.Text(label, Billable is null ? "" : "brand", "medium").Centered(), Ui.Icon("IconChevronDown", "secondary", "small").Centered());
        var menu = new MenuFlyout();
        foreach (var (text, value) in new (string, bool?)[] { ("Billable and non-billable", null), ("Billable", true), ("Non-billable", false) })
        {
            var item = new MenuItem { Header = text, Icon = Billable == value ? Ui.Icon("IconCheckmark") : null };
            item.Click += (_, _) =>
            {
                Billable = value;
                Page = 1;
                Changed();
            };
            menu.Items.Add(item);
        }
        return new Button { Content = content, Flyout = menu }.Classed("field").Spoken("Billable").Named("FilterBillable");
    }

    private Button RoundingButton()
    {
        var label = Rounding switch
        {
            RoundingMode.None => "No rounding",
            RoundingMode.Up => "Round up",
            RoundingMode.Down => "Round down",
            _ => "Round to nearest",
        } + (Rounding == RoundingMode.None ? "" : RoundingMinutes == 60 ? " hour" : $" {RoundingMinutes} min");
        var menu = new MenuFlyout();
        foreach (var (text, value) in new[] { ("No rounding", RoundingMode.None), ("Round up", RoundingMode.Up), ("Round to nearest", RoundingMode.Nearest), ("Round down", RoundingMode.Down) })
        {
            var item = new MenuItem { Header = text, Icon = Rounding == value ? Ui.Icon("IconCheckmark") : null };
            item.Click += (_, _) =>
            {
                Rounding = value;
                Changed();
            };
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var to = new MenuItem { Header = "To", IsEnabled = Rounding != RoundingMode.None };
        foreach (var minutes in new[] { 1, 5, 6, 10, 12, 15, 30, 60 })
        {
            var item = new MenuItem { Header = minutes == 60 ? "1 hour" : $"{minutes} minutes", Icon = RoundingMinutes == minutes ? Ui.Icon("IconCheckmark") : null };
            item.Click += (_, _) =>
            {
                RoundingMinutes = minutes;
                Changed();
            };
            to.Items.Add(item);
        }
        menu.Items.Add(to);
        return new Button { Content = Ui.Row(6, Ui.Icon("IconArrowSync", "secondary", "small").Centered(), Ui.Text(label, Rounding == RoundingMode.None ? "" : "brand").Centered()), Flyout = menu }
            .Classed("field").Tip("Rounds each entry's duration in the report; the entries themselves stay as they are.").Spoken("Rounding");
    }

    private MenuFlyout PeriodMenu()
    {
        var menu = new MenuFlyout();
        var periods = Kind == ReportKind.Weekly
            ? new[] { ReportPeriod.ThisWeek, ReportPeriod.LastWeek }
            : [ReportPeriod.Today, ReportPeriod.Yesterday, ReportPeriod.ThisWeek, ReportPeriod.LastWeek, ReportPeriod.ThisMonth, ReportPeriod.LastMonth, ReportPeriod.ThisYear, ReportPeriod.LastYear];
        foreach (var choice in periods)
        {
            var item = new MenuItem { Header = DayRange.Label(choice), Icon = Period == choice ? Ui.Icon("IconCheckmark") : null };
            item.Click += (_, _) => Choose(choice);
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var custom = new MenuItem { Header = Kind == ReportKind.Weekly ? "Choose a week…" : "Custom range…" };
        custom.Click += (_, _) => Dispatcher.UIThread.Post(() => CustomRange().ShowAt(period));
        menu.Items.Add(custom);
        return menu;
    }

    internal void Choose(ReportPeriod choice)
    {
        Period = choice;
        Custom = null;
        Page = 1;
        Changed();
    }

    private Flyout CustomRange()
    {
        var range = Range;
        var from = new Calendar { SelectionMode = CalendarSelectionMode.SingleDate, SelectedDate = range.From.ToDateTime(TimeOnly.MinValue), DisplayDate = range.From.ToDateTime(TimeOnly.MinValue) };
        var to = new Calendar { SelectionMode = CalendarSelectionMode.SingleDate, SelectedDate = range.To.ToDateTime(TimeOnly.MinValue), DisplayDate = range.To.ToDateTime(TimeOnly.MinValue) };
        var flyout = new Flyout();
        var show = Ui.Button("Show", () =>
        {
            var first = DateOnly.FromDateTime(from.SelectedDate ?? DateTime.Today);
            var last = Kind == ReportKind.Weekly ? first : DateOnly.FromDateTime(to.SelectedDate ?? DateTime.Today);
            Custom = new DayRange(first, last < first ? first : last);
            Period = ReportPeriod.Custom;
            Page = 1;
            flyout.Hide();
            Changed();
        }, "primary");
        show.HorizontalAlignment = HorizontalAlignment.Right;
        var calendars = Kind == ReportKind.Weekly
            ? Ui.Stack(6, Ui.Text("Week of", "medium"), from)
            : (Control)Ui.Row(12, Ui.Stack(6, Ui.Text("From", "medium"), from), Ui.Stack(6, Ui.Text("To", "medium"), to));
        flyout.Content = Ui.Stack(12, calendars, show);
        return flyout;
    }

    private MenuFlyout ExportMenu()
    {
        var menu = new MenuFlyout();
        foreach (var (label, format) in new[] { ("CSV", "csv"), ("Excel", "xlsx"), ("PDF", "pdf") })
        {
            var item = new MenuItem { Header = label };
            item.Click += async (_, _) =>
            {
                try
                {
                    await ExportAsync(format);
                }
                catch (Exception exception)
                {
                    owner.Notify(exception.Message.Length > 0 ? exception.Message : "Couldn't export the report.");
                }
            };
            menu.Items.Add(item);
        }
        return menu;
    }

    // Requests

    private Guid[] Ids(HashSet<Guid> set, bool keepNone = true) => set.Where(id => keepNone || id != None).ToArray();
    private Guid[] UserIds => Members.Where(id => !groups.Any(g => g.Id == id)).ToArray();
    private Guid[] GroupIds => Members.Where(id => groups.Any(g => g.Id == id)).ToArray();
    private string? Description => (description.Text ?? "").Trim() is { Length: > 0 } text ? text : null;

    internal SummaryReportRequest SummaryRequest() => new()
    {
        From = Range.From, To = Range.To, UserIds = UserIds, GroupIds = GroupIds, ProjectIds = Ids(Projects), ClientIds = Ids(Clients), TagIds = Ids(Tags),
        Billable = Billable, Description = Description, Rounding = Rounding, RoundingMinutes = RoundingMinutes, GroupBy = GroupBy, SubGroupBy = SubGroupBy,
    };

    internal DetailedReportRequest DetailedRequest() => new()
    {
        From = Range.From, To = Range.To, UserIds = UserIds, GroupIds = GroupIds, ProjectIds = Ids(Projects), ClientIds = Ids(Clients), TagIds = Ids(Tags),
        Billable = Billable, Description = Description, Rounding = Rounding, RoundingMinutes = RoundingMinutes, Sort = Sort, Descending = Descending, Page = Page, PageSize = 50,
    };

    internal WeeklyReportRequest WeeklyRequest() => new()
    {
        From = Range.From, To = Range.To, UserIds = UserIds, GroupIds = GroupIds, ProjectIds = Ids(Projects), ClientIds = Ids(Clients), TagIds = Ids(Tags),
        Billable = Billable, Description = Description, Rounding = Rounding, RoundingMinutes = RoundingMinutes, GroupBy = WeeklyGroupBy,
    };

    internal async Task RunAsync()
    {
        if (owner.Api is not { } api) return;
        var ticket = ++generation;
        content.Opacity = 0.6;
        try
        {
            switch (Kind)
            {
                case ReportKind.Summary:
                    var summary = await api.GetSummaryReportAsync(owner.WorkspaceId, SummaryRequest());
                    if (ticket == generation) ShowSummary(summary);
                    break;
                case ReportKind.Detailed:
                    var detailed = await api.GetDetailedReportAsync(owner.WorkspaceId, DetailedRequest());
                    if (ticket == generation) ShowDetailed(detailed);
                    break;
                default:
                    var weekly = await api.GetWeeklyReportAsync(owner.WorkspaceId, WeeklyRequest());
                    if (ticket == generation) ShowWeekly(weekly);
                    break;
            }
        }
        catch (Exception exception)
        {
            if (ticket != generation) return;
            content.Children.Clear();
            var retry = Ui.Button("Try again", RunAsync, owner.Notify);
            var failed = Ui.Stack(8, Ui.Icon("IconWarningFilled", "secondary", "large"), Ui.Text("Couldn't load the report", "bodyLarge"), Ui.Wrapped(exception.Message, "secondary"), retry);
            foreach (var child in failed.Children) child.HorizontalAlignment = HorizontalAlignment.Center;
            failed.Margin = new Thickness(0, 60);
            content.Children.Add(failed);
        }
        finally
        {
            if (ticket == generation) content.Opacity = 1;
        }
    }

    private async Task ExportAsync(string format)
    {
        if (owner.Api is not { } api) return;
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null || !storage.CanSave) throw new InvalidOperationException("Saving files isn't available here.");
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export report",
            SuggestedFileName = $"spindle-{Kind.ToString().ToLowerInvariant()}-{Range.From:yyyy-MM-dd}.{format}",
            DefaultExtension = format,
            FileTypeChoices = [new FilePickerFileType(format.ToUpperInvariant()) { Patterns = ["*." + format] }],
        });
        if (file is null) return;
        var export = Kind switch
        {
            ReportKind.Summary => await api.ExportSummaryReportAsync(owner.WorkspaceId, SummaryRequest(), format),
            ReportKind.Detailed => await api.ExportDetailedReportAsync(owner.WorkspaceId, DetailedRequest(), format),
            _ => await api.ExportWeeklyReportAsync(owner.WorkspaceId, WeeklyRequest(), format),
        };
        await using var stream = await file.OpenWriteAsync();
        stream.SetLength(0);
        await stream.WriteAsync(export.Content);
        owner.Notify("Report exported", NoticeKind.Success);
    }

    // Results

    private string Duration(long seconds) => owner.Time.Duration(seconds);

    /// <summary>The figures above every report: total, billable or billed, amount and profit.</summary>
    private Control Totals(ReportTotals totals, string currency)
    {
        var tiles = new List<Control>();
        Control Tile(string label, string value, string note) => Ui.Card(Ui.Stack(2, Ui.Text(label, "secondary"), Ui.Text(value, "subtitle", "mono").Named("Total" + label), Ui.Wrapped(note, "caption", "secondary")));
        tiles.Add(Tile("Total", Duration(totals.Seconds), totals.EntryCount == 1 ? "1 entry" : $"{totals.EntryCount} entries"));
        if (totals.BilledSeconds is { } billed && billed != totals.BillableSeconds)
        {
            var ratio = totals.Seconds > 0 ? $"{(double)billed / totals.Seconds:0.00}× the time tracked · " : "";
            tiles.Add(Tile("Billed", (billed < 0 ? "−" : "") + Duration(Math.Abs(billed)), $"{ratio}{Duration(totals.BillableSeconds)} billable tracked"));
        }
        else
        {
            tiles.Add(Tile("Billable", Duration(totals.BillableSeconds), $"{ReportFormat.Percent(totals.BillableSeconds, totals.Seconds)} of the total"));
        }
        if (totals.Amount is { } amount)
            tiles.Add(Tile("Amount", ReportFormat.Money(amount, currency), totals.Seconds > 0 ? $"{ReportFormat.Money(amount / (decimal)(totals.Seconds / 3600d), currency)}/h per hour tracked" : "Billed time at its rates"));
        if (totals.LaborCost is > 0 && totals.Amount is { } revenue)
            tiles.Add(Tile("Profit", ReportFormat.Money(revenue - totals.LaborCost.Value, currency), $"{ReportFormat.Money(totals.LaborCost, currency)} labor cost · {ReportFormat.Percent((double)(revenue - totals.LaborCost.Value), (double)revenue)} margin"));
        var grid = new Avalonia.Controls.Primitives.UniformGrid { Columns = tiles.Count, Rows = 1 };
        foreach (var tile in tiles) grid.Children.Add(tile.Margin(0, 0, 12, 0));
        return grid;
    }

    private string EmptyText(string start) => start + (Filtered ? " with these filters." : ".");

    private static Control Empty(string text) => new Border { Child = Ui.Text(text, "secondary"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 40) };

    private static Control CardWith(string title, Control? accessory, Control body)
    {
        var header = Ui.Columns("*,Auto", 12, Ui.Text(title, "bodyLarge").Centered(), accessory);
        return Ui.Card(Ui.Stack(14, header, body));
    }

    private void ShowSummary(SummaryReport report)
    {
        content.Children.Clear();
        content.Children.Add(Totals(report.Totals, report.Currency));
        content.Children.Add(CardWith($"Time by {report.Resolution.ToString().ToLowerInvariant()}", null, new TimeChart(report.Buckets, report.Resolution)));
        var groupBy = Choice(Enum.GetValues<ReportGrouping>(), GroupBy, value =>
        {
            GroupBy = value;
            if (SubGroupBy == value) SubGroupBy = null;
            Changed();
        }, Grouping);
        var thenOptions = new List<ReportGrouping?> { null };
        thenOptions.AddRange(Enum.GetValues<ReportGrouping>().Where(g => g != GroupBy).Cast<ReportGrouping?>());
        var then = Choice(thenOptions.ToArray(), SubGroupBy, value =>
        {
            SubGroupBy = value;
            Changed();
        }, g => g is { } value ? Grouping(value) : "Nothing");
        var accessory = Ui.Row(8, Ui.Text("Group by", "secondary").Centered(), groupBy, Ui.Text("then", "secondary").Centered(), then);
        Control body;
        if (report.Groups.Length == 0)
        {
            body = Empty(EmptyText("No time tracked in this period"));
        }
        else
        {
            var colors = report.Groups.Select((g, i) => ReportFormat.Color(g.Key, g.Color, i)).ToArray();
            var donut = new Donut(report.Groups.Select((g, i) => ((double)g.Seconds, colors[i])).ToArray(), Durations.HoursMinutes(report.Totals.Seconds));
            body = Ui.Columns("Auto,*", 28, donut.Centered(), GroupTable(report, colors));
        }
        content.Children.Add(CardWith("Breakdown", accessory, body));
    }

    private Control GroupTable(SummaryReport report, string?[] colors)
    {
        var money = report.Totals.Amount is not null;
        var columns = money ? "*,90,150,100" : "*,90,150";
        var table = Ui.Stack(0);
        var expandable = report.Groups.Any(g => g.SubGroups.Length > 0);
        Control? toggle = expandable ? Ui.Button(expanded.Count > 0 ? "Collapse all" : "Expand all", () =>
        {
            if (expanded.Count > 0) expanded.Clear();
            else foreach (var group in report.Groups) expanded.Add(group.Key);
            ShowSummary(report);
        }, "link") : null;
        var header = Ui.Columns(columns, 12, Ui.Row(10, Ui.Text(Grouping(GroupBy), "caption", "medium", "secondary").Centered(), toggle), Right(Ui.Text("Duration", "caption", "medium", "secondary")),
            Ui.Text("Share", "caption", "medium", "secondary").Margin(12, 0, 0, 0), money ? Right(Ui.Text("Amount", "caption", "medium", "secondary")) : null);
        table.Children.Add(header.Margin(0, 0, 0, 6));
        for (var index = 0; index < report.Groups.Length; index++)
        {
            var group = report.Groups[index];
            table.Children.Add(Ui.Divider());
            var open = expanded.Contains(group.Key);
            var chevron = group.SubGroups.Length == 0 ? (Control)new Border { Width = 16 } : Ui.Icon(open ? "IconChevronDown" : "IconChevronRight", "secondary", "small").Centered();
            var name = Ui.Row(8, chevron, Ui.Dot(colors[index], 9), Ui.Text(group.Name, group.Key == "none" ? "secondary" : ""), group.Detail is null ? null : Ui.Text(group.Detail, "secondary"));
            var row = Ui.Columns(columns, 12, name.Centered(), Right(Ui.Text(Duration(group.Seconds), "mono")), Share(group.Seconds, report.Totals.Seconds, 0.55),
                money ? Right(Ui.Text(ReportFormat.Money(group.Amount, report.Currency), "mono")) : null);
            row.Margin = new Thickness(0, 7);
            if (group.SubGroups.Length > 0)
            {
                var key = group.Key;
                var button = new Button { Content = row, Padding = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch }.Classed("row");
                button.Click += (_, _) =>
                {
                    if (!expanded.Remove(key)) expanded.Add(key);
                    ShowSummary(report);
                };
                table.Children.Add(button);
            }
            else
            {
                table.Children.Add(row);
            }
            if (!open) continue;
            foreach (var child in group.SubGroups)
            {
                var sub = Ui.Columns(columns, 12, Ui.Text(child.Name, child.Key == "none" ? "secondary" : "").Margin(29, 0, 0, 0), Right(Ui.Text(Duration(child.Seconds), "mono")),
                    Share(child.Seconds, report.Totals.Seconds, 0.25), money ? Right(Ui.Text(ReportFormat.Money(child.Amount, report.Currency), "mono")) : null);
                sub.Margin = new Thickness(0, 5);
                table.Children.Add(sub);
            }
        }
        return table;
    }

    private static Control Right(TextBlock text)
    {
        text.HorizontalAlignment = HorizontalAlignment.Right;
        text.VerticalAlignment = VerticalAlignment.Center;
        return text;
    }

    private Control Share(long seconds, long total, double opacity)
    {
        var fraction = total > 0 ? Math.Clamp((double)seconds / total, 0, 1) : 0;
        var track = new Grid { Height = 6, VerticalAlignment = VerticalAlignment.Center };
        track.ColumnDefinitions.Add(new ColumnDefinition(Math.Max(fraction, 0.0001), GridUnitType.Star));
        track.ColumnDefinitions.Add(new ColumnDefinition(Math.Max(1 - fraction, 0.0001), GridUnitType.Star));
        var fill = new Border { CornerRadius = new CornerRadius(3) };
        fill.Res(Border.BackgroundProperty, "TextPrimaryBrush");
        fill.Opacity = opacity;
        track.Children.Add(fill);
        var percent = Ui.Text(ReportFormat.Percent(seconds, total), "caption", "secondary", "mono");
        percent.Width = 40;
        percent.TextAlignment = TextAlignment.Right;
        return Ui.Columns("*,Auto", 6, track, percent).Margin(12, 0, 0, 0);
    }

    private static string Grouping(ReportGrouping grouping) => grouping == ReportGrouping.User ? "Member" : grouping.ToString();

    private static ComboBox Choice<T>(T[] values, T selected, Action<T> changed, Func<T, string> label)
    {
        var box = new ComboBox { ItemsSource = values.Select(label).ToArray(), SelectedIndex = Math.Max(0, Array.IndexOf(values, selected)), MinWidth = 120 };
        box.SelectionChanged += (_, _) =>
        {
            if (box.SelectedIndex >= 0 && !Equals(values[box.SelectedIndex], selected)) changed(values[box.SelectedIndex]);
        };
        return box;
    }

    private void ShowDetailed(DetailedReport report)
    {
        content.Children.Clear();
        content.Children.Add(Totals(report.Totals, report.Currency));
        var money = report.Entries.Any(e => e.Amount is not null) || report.Totals.Amount is not null;
        var columns = "*,170," + (owner.IsAdmin ? "130," : "") + "150,80" + (money ? ",90" : "");
        Control Header(string label, ReportSort? sort, bool right = false)
        {
            if (sort is not { } key) return right ? Right(Ui.Text(label, "caption", "medium", "secondary")) : Ui.Text(label, "caption", "medium", "secondary");
            var active = Sort == key;
            var text = Ui.Row(4, Ui.Text(label, "caption", "medium", active ? "" : "secondary"), active ? Ui.Icon(Descending ? "IconChevronDown" : "IconChevronDown", "small").Classed("secondary") : null);
            if (active && !Descending) text.Children[^1].RenderTransform = new RotateTransform(180);
            var button = Ui.Button(text, () =>
            {
                if (Sort == key) Descending = !Descending;
                else
                {
                    Sort = key;
                    Descending = key is ReportSort.Date or ReportSort.Duration;
                }
                Page = 1;
                Changed();
            }, "subtle");
            button.Padding = new Thickness(4, 2);
            button.MinHeight = 0;
            button.HorizontalAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            return button;
        }
        var cells = new List<Control?> { Header("Description", ReportSort.Description), Header("Project", ReportSort.Project) };
        if (owner.IsAdmin) cells.Add(Header("Member", ReportSort.User));
        cells.Add(Header("Date", ReportSort.Date));
        cells.Add(Header("Duration", ReportSort.Duration, true));
        if (money) cells.Add(Header("Amount", null, true));
        var list = Ui.Stack(0, Ui.Columns(columns, 12, cells.ToArray()).Margin(0, 0, 0, 6));
        if (report.Entries.Length == 0) list.Children.Add(Empty(EmptyText("No time entries in this period")));
        foreach (var entry in report.Entries)
        {
            list.Children.Add(Ui.Divider());
            var details = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            if (entry.IsBillable) details.Children.Add(Ui.Icon("IconMoney", "secondary", "small").Tip("Billable"));
            foreach (var tag in entry.Tags) details.Children.Add(Ui.Chip(tag));
            var what = Ui.Stack(3, Ui.Wrapped(entry.Description.Length == 0 ? "No description" : entry.Description, entry.Description.Length == 0 ? "secondary" : ""), details.Children.Count > 0 ? details : null);
            Control project = entry.ProjectName is null
                ? Ui.Text("No project", "secondary")
                : Ui.Columns("Auto,*", 6, Ui.Dot(entry.ProjectColor, 9).Margin(0, 5, 0, 0), Ui.Stack(1, Ui.Text(entry.ProjectName + (entry.Percent < 100 ? $" {entry.Percent}%" : "")),
                    (entry.TaskName ?? entry.ClientName) is { } detail ? Ui.Text(detail, "caption", "secondary") : null));
            var when = Ui.Stack(1, Ui.Text(owner.Time.ShortDay(owner.Time.Day(entry.Start)), "mono"), Ui.Text($"{owner.Time.Clock(entry.Start)} – {owner.Time.Clock(entry.Stop)}", "caption", "secondary", "mono"));
            var row = new List<Control?> { what, project };
            if (owner.IsAdmin) row.Add(Ui.Text(entry.UserName));
            row.Add(when);
            row.Add(Right(Ui.Text(Duration(entry.Seconds), "mono")));
            if (money) row.Add(Right(Ui.Text(ReportFormat.Money(entry.Amount, report.Currency), "mono")));
            list.Children.Add(Ui.Columns(columns, 12, row.ToArray()).Margin(0, 8, 0, 8));
        }
        content.Children.Add(Ui.Card(list));
        var pages = Math.Max(1, (int)Math.Ceiling(report.TotalCount / 50d));
        if (pages > 1)
        {
            var first = (Page - 1) * 50 + 1;
            var last = Math.Min(report.TotalCount, Page * 50);
            var previous = Ui.Button(Ui.Labeled("IconChevronLeft", "Previous"), () =>
            {
                Page--;
                Changed();
            });
            previous.IsEnabled = Page > 1;
            var next = Ui.Button(Ui.Row(8, Ui.Text("Next"), Ui.Icon("IconChevronRight").Centered()), () =>
            {
                Page++;
                Changed();
            });
            next.IsEnabled = Page < pages;
            content.Children.Add(Ui.Columns("*,Auto,Auto,Auto", 10, Ui.Text($"{first}–{last} of {report.TotalCount} entries", "secondary").Centered(), previous, Ui.Text($"Page {Page} of {pages}").Centered(), next));
        }
    }

    private void ShowWeekly(WeeklyReport report)
    {
        content.Children.Clear();
        content.Children.Add(Totals(report.Totals, report.Currency));
        var groupings = Enum.GetValues<ReportGrouping>().Where(g => g != ReportGrouping.Description).ToArray();
        var rowsBy = Choice(groupings, WeeklyGroupBy, value =>
        {
            WeeklyGroupBy = value;
            Changed();
        }, Grouping);
        var money = report.Totals.Amount is not null;
        var columns = "*," + string.Join(",", report.Days.Select(_ => "80")) + ",90" + (money ? ",100" : "");
        var english = BritishEnglish.Culture;
        var header = new List<Control?> { Ui.Text(Grouping(WeeklyGroupBy), "caption", "medium", "secondary") };
        header.AddRange(report.Days.Select(d => Right(Ui.Text(d.ToString("ddd d", english), "caption", "medium", "secondary"))));
        header.Add(Right(Ui.Text("Total", "caption", "medium", "secondary")));
        if (money) header.Add(Right(Ui.Text("Amount", "caption", "medium", "secondary")));
        var table = Ui.Stack(0, Ui.Columns(columns, 12, header.ToArray()).Margin(0, 0, 0, 6));
        if (report.Rows.Length == 0) table.Children.Add(Empty(EmptyText("No time tracked this week")));
        for (var index = 0; index < report.Rows.Length; index++)
        {
            var row = report.Rows[index];
            table.Children.Add(Ui.Divider());
            var cells = new List<Control?> { Ui.Row(8, Ui.Dot(ReportFormat.Color(row.Key, row.Color, index), 8), Ui.Text(row.Name), row.Detail is null ? null : Ui.Text(row.Detail, "secondary")) };
            cells.AddRange(row.Days.Select(seconds => Right(seconds == 0 ? Ui.Text("–", "secondary") : Ui.Text(Duration(seconds), "mono"))));
            cells.Add(Right(Ui.Text(Duration(row.Seconds), "strong", "mono")));
            if (money) cells.Add(Right(Ui.Text(ReportFormat.Money(row.Amount, report.Currency), "mono")));
            table.Children.Add(Ui.Columns(columns, 12, cells.ToArray()).Margin(0, 7, 0, 7));
        }
        if (report.Rows.Length > 0)
        {
            table.Children.Add(Ui.Divider());
            var footer = new List<Control?> { Ui.Text("Total", "strong") };
            footer.AddRange(report.DayTotals.Select(seconds => Right(Ui.Text(Duration(seconds), "strong", "mono"))));
            footer.Add(Right(Ui.Text(Duration(report.Totals.Seconds), "strong", "mono")));
            if (money) footer.Add(Right(Ui.Text(ReportFormat.Money(report.Totals.Amount, report.Currency), "mono")));
            table.Children.Add(Ui.Columns(columns, 12, footer.ToArray()).Margin(0, 7, 0, 0));
        }
        content.Children.Add(CardWith("This week", Ui.Row(8, Ui.Text("Rows", "secondary").Centered(), rowsBy), new ScrollViewer { Content = table, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto }));
    }
}
