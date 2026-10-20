using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Spindle.Activity;
using Spindle.Core;
using Spindle.Windows.Model;

namespace Spindle.Windows.Views;

/// <summary>
/// Right after agent logs are turned on: what the last week's logs found in the time already tracked, the repositories
/// to link to projects, and the way into that week's review. So the point shows in the first minutes, not when the
/// first parallel entry stops. As the Mac's AgentLogsOnboarding. Closes with the review's days to open it.
/// </summary>
public sealed class AgentLogsOnboarding : UserControl
{
    public enum Step { Found, Link, Review }

    /// <summary>The days the finding is about, and the review opens on.</summary>
    public const int FindingDays = 7;
    /// <summary>The days whose repositories are offered for linking.</summary>
    public const int RepositoryDays = 14;

    private readonly MainWindow owner;
    private readonly DateOnly today;
    /// <summary>The project chosen for each repository, by its identity; null for none.</summary>
    private readonly Dictionary<string, Guid?> choices = [];
    private Findings? findings;

    /// <summary>What the logs found, worked out once they are read.</summary>
    /// <param name="Tracked">All the time tracked in those days, finished entries only.</param>
    public sealed record Findings(ElsewhereFinding Elsewhere, long Tracked, long Untracked, IReadOnlyList<RepositoryFinding> Repositories);

    /// <param name="today">The last day of the week it reads; today, unless snapshots show a day of synthetic logs.</param>
    public AgentLogsOnboarding(MainWindow owner, Step step = Step.Found, DateOnly? today = null)
    {
        this.owner = owner;
        this.today = today ?? owner.Time.Today();
        Current = step;
        Name = "AgentLogsOnboarding";
        Render();
        AttachedToVisualTree += async (_, _) => await LoadAsync();
    }

    public Step Current { get; private set; }
    public Findings? Found => findings;

    private UserTime Time => owner.Time;
    private DateOnly[] Week => Enumerable.Range(0, FindingDays).Select(i => today.AddDays(i - FindingDays + 1)).ToArray();
    private DateOnly[] Fortnight => Enumerable.Range(0, RepositoryDays).Select(i => today.AddDays(i - RepositoryDays + 1)).ToArray();

    public async Task LoadAsync()
    {
        try
        {
            await owner.Desktop.LoadReviewAsync(Week);
            await owner.LoadEntriesAsync(Time.StartOf(Fortnight[0]), Time.StartOf(Week[^1].AddDays(1)));
            await owner.Splits.LoadAsync(Fortnight);
        }
        catch (Exception exception)
        {
            owner.Notify(exception.Message);
        }
        Work();
        Render();
    }

    /// <summary>Reads what the logs say about the days loaded, by the same rules as the suggestions.</summary>
    private void Work()
    {
        var splits = owner.Splits;
        var matcher = splits.Matcher();
        var since = Time.StartOf(Week[0]);
        var finished = owner.Entries.Where(e => e.Stop is not null && e.Start >= since).ToList();
        var elsewhere = ActivityFindings.Elsewhere(
            finished.Select(e => new ActivityFindings.Entry(e.Start.ToUnixTimeMilliseconds(), e.Stop!.Value.ToUnixTimeMilliseconds(), e.ProjectId, e.IsSplit())),
            splits.Attention(Week), splits.Placing());
        var untracked = owner.Desktop.Untracked(Week).Sum(b => (b.End - b.Start).TotalMilliseconds);
        var repositories = ActivityFindings.Repositories(splits.Attention(Fortnight));
        foreach (var found in repositories) choices.TryAdd(found.Repository.Identity, matcher(found.Repository));
        var tracked = finished.Sum(e => (e.Stop!.Value - e.Start).TotalMilliseconds);
        findings = new Findings(elsewhere, (long)tracked, (long)untracked, repositories);
    }

    private void Render()
    {
        Control body;
        if (findings is null)
        {
            var reading = Ui.Stack(12, new ProgressBar { IsIndeterminate = true, Width = 160 }, Ui.Text($"Reading the last {FindingDays} days of {AgentLogFormat.Names} logs…", "secondary"));
            foreach (var child in reading.Children) child.HorizontalAlignment = HorizontalAlignment.Center;
            reading.Margin = new Thickness(0, 100);
            body = reading;
        }
        else
        {
            body = Current switch
            {
                Step.Link => LinkStep(findings),
                Step.Review => ReviewStep(),
                _ => FoundStep(findings),
            };
        }
        body.Margin = new Thickness(32, 28, 32, 24);
        var footer = new Border { Child = Footer(), Padding = new Thickness(24, 14) }.Res(Border.BackgroundProperty, "CardBackgroundSecondaryBrush");
        Content = new DockPanel { Children = { footer.Dock(Dock.Bottom), Ui.Divider().Dock(Dock.Bottom), body } };
    }

    // Steps

    private Control FoundStep(Findings found)
    {
        var elsewhere = found.Elsewhere;
        var stack = Ui.Stack(0, Ui.Text($"LAST {FindingDays} DAYS", "caption", "strong", "brand"));
        if (elsewhere.OtherProjects > 0)
        {
            var number = Ui.Text(Durations.HoursMinutes(elsewhere.OtherProjects / 1000d), "brand", "mono").Named("FoundElsewhere");
            number.FontSize = 40;
            number.FontWeight = FontWeight.SemiBold;
            stack.Children.Add(number.Margin(0, 8, 0, 0));
            stack.Children.Add(Ui.Wrapped("of tracked time went to other projects than the one on the timer.", "bodyLarge"));
        }
        else
        {
            var sentence = elsewhere.Entries > 0 || elsewhere.Unlinked > 0 ? "No tracked time went to another project."
                : found.Repositories.Count == 0 ? $"No agent work in the logs in the last {FindingDays} days."
                : $"No tracked time overlaps agent work in the last {FindingDays} days.";
            stack.Children.Add(Ui.Wrapped(sentence, "subtitle").Margin(0, 10, 0, 0));
        }
        if (found.Tracked > 0)
        {
            stack.Children.Add(ShareBar(found.Tracked, elsewhere.OtherProjects, elsewhere.Unlinked).Margin(0, 18, 0, 0));
            var share = (int)Math.Round(elsewhere.OtherProjects * 100d / found.Tracked);
            var caption = $"{share}% of {Durations.HoursMinutes(found.Tracked / 1000d)} tracked" + (elsewhere.Unlinked > 0 ? " · hatched: repositories without a project" : "");
            stack.Children.Add(Ui.Text(caption, "caption", "secondary", "mono").Margin(0, 6, 0, 0));
        }
        var tiles = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*"), ColumnSpacing = 10, Margin = new Thickness(0, 22, 0, 0) };
        tiles.Children.Add(Tile("IconArrowSplit", $"{elsewhere.Entries}", elsewhere.Entries == 1 ? "entry with a split to offer" : "entries with a split to offer").Column(0));
        tiles.Children.Add(Tile("IconLink", Durations.HoursMinutes(elsewhere.Unlinked / 1000d), "in repositories without a project yet").Column(1));
        tiles.Children.Add(Tile("IconClock", Durations.HoursMinutes(found.Untracked / 1000d), "of agent work with no timer running").Column(2));
        stack.Children.Add(tiles);
        stack.Children.Add(Ui.Columns("Auto,*", 6, Ui.Icon("IconInfoFilled", "secondary", "small"),
            Ui.Wrapped("Counted by where your prompts went. Time without prompts stays on the timer's project. Nothing changes until you take a suggestion.", "caption", "secondary")).Margin(0, 20, 0, 0));
        return stack;
    }

    /// <summary>
    /// Tracked time as one bar: the part that went to other projects in marigold, the part in repositories without a
    /// project hatched, the rest quiet.
    /// </summary>
    private static Control ShareBar(long tracked, long elsewhere, long unlinked)
    {
        var other = Math.Min(1, elsewhere / (double)tracked);
        var open = Math.Min(1 - other, unlinked / (double)tracked);
        var grid = new Grid { Height = 10, ClipToBounds = true };
        foreach (var share in new[] { other, open, 1 - other - open }) grid.ColumnDefinitions.Add(new ColumnDefinition(Math.Max(0.0001, share), GridUnitType.Star));
        grid.Children.Add(new Border().Res(Border.BackgroundProperty, "BrandBrush").Column(0));
        if (open > 0)
        {
            var hatched = new Rectangle { StrokeThickness = 1, StrokeDashArray = [2, 2], Margin = new Thickness(2, 0, 0, 0) }
                .Res(Shape.StrokeProperty, "BrandBrush").Res(Shape.FillProperty, "BrandTintBrush");
            grid.Children.Add(hatched.Column(1));
        }
        grid.Children.Add(new Border().Res(Border.BackgroundProperty, "ChipBackgroundBrush").Column(2));
        return new Border { Child = grid, CornerRadius = new CornerRadius(5), ClipToBounds = true };
    }

    /// <summary>One number and what it counts.</summary>
    private static Control Tile(string icon, string value, string caption)
    {
        var number = Ui.Text(value, "subtitle", "mono");
        var stack = Ui.Stack(4, Ui.Icon(icon, "secondary"), number.Margin(0, 2, 0, 0), Ui.Wrapped(caption, "caption", "secondary"));
        stack.Children[0].HorizontalAlignment = HorizontalAlignment.Left;
        return new Border { Child = stack, Padding = new Thickness(12), CornerRadius = new CornerRadius(8) }.Res(Border.BackgroundProperty, "SubtleFillHoverBrush");
    }

    private Control LinkStep(Findings found)
    {
        var most = Math.Max(1, found.Repositories.Select(r => r.Attention).DefaultIfEmpty(1).Max());
        var rows = Ui.Stack(0);
        for (var index = 0; index < found.Repositories.Count; index++)
        {
            if (index > 0) rows.Children.Add(Ui.Divider().Margin(34, 0, 0, 0));
            rows.Children.Add(RepositoryRow(found.Repositories[index], most));
        }
        var list = new Border { Child = new ScrollViewer { Content = rows, MaxHeight = 300 }, Padding = new Thickness(12, 0), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1) }
            .Res(Border.BorderBrushProperty, "CardStrokeBrush").Res(Border.BackgroundProperty, "CardBackgroundBrush");
        return Ui.Stack(4, Ui.Text("Link repositories to projects", "subtitle"),
            Ui.Wrapped($"Agent work in the last {RepositoryDays} days, most first. Guesses come from repository names and past entries. Links sync to your account.", "secondary"),
            list.Margin(0, 12, 0, 0));
    }

    private Control RepositoryRow(RepositoryFinding found, long most)
    {
        var repository = found.Repository;
        var projects = owner.Catalog.Projects.Where(p => p.ArchivedAt is null).ToArray();
        var choice = choices.GetValueOrDefault(repository.Identity);
        var color = owner.Catalog.Project(choice)?.Color ?? ActivityColors.Of(repository.Identity);
        var name = Ui.Row(8, Ui.Text(repository.Name, "strong"));
        if (repository.Identity.StartsWith("origin:", StringComparison.Ordinal))
        {
            var origin = Ui.Text(repository.Identity["origin:".Length..], "caption", "tertiary");
            origin.TextTrimming = TextTrimming.CharacterEllipsis;
            origin.MaxWidth = 200;
            name.Children.Add(origin.Centered());
        }
        var bar = new Border { Height = 4, Width = Math.Max(4, 120d * found.Attention / most), CornerRadius = new CornerRadius(2), Background = Ui.ProjectBrush(color, 0.7), HorizontalAlignment = HorizontalAlignment.Left };
        var amount = Ui.Row(8, new Border { Width = 120, Child = bar }.Centered(), Ui.Text(Durations.HoursMinutes(found.Attention / 1000d), "caption", "secondary", "mono"));
        var picker = new ComboBox { Width = 180, ItemsSource = new[] { "No project" }.Concat(projects.Select(p => p.Name)).ToArray(), Name = "RepositoryProject" }
            .Spoken($"Project for {repository.Name}");
        picker.SelectedIndex = choice is { } id ? Array.FindIndex(projects, p => p.Id == id) + 1 : 0;
        picker.SelectionChanged += (_, _) =>
        {
            choices[repository.Identity] = picker.SelectedIndex > 0 ? projects[picker.SelectedIndex - 1].Id : null;
            Render();
        };
        // A repository whose branches belong to different projects, e.g. a monorepo; saving its rules links it too.
        var branches = BranchRulesButton.Create(owner, repository, @short: true, saved: () =>
        {
            choices[repository.Identity] = owner.Splits.Chosen(repository);
            Render();
        }, project: choice);
        branches.HorizontalAlignment = HorizontalAlignment.Left;
        var row = Ui.Columns("Auto,*,Auto", 12, Ui.Dot(color, 10), Ui.Stack(3, name, amount, branches), picker.Centered());
        row.Margin = new Thickness(0, 10);
        return row;
    }

    private Control ReviewStep()
    {
        var items = owner.Desktop.Review(Week);
        var stack = Ui.Stack(0, Ui.Text(items.Count == 0 ? $"Nothing to review from the last {FindingDays} days"
            : $"{items.Count} {(items.Count == 1 ? "suggestion" : "suggestions")} from the last {FindingDays} days", "subtitle").Named("OnboardingReviewTitle"));
        if (items.Count > 0) stack.Children.Add(Ui.Text(ReviewItem.Breakdown(items), "secondary").Margin(0, 4, 0, 0));
        var ways = Ui.Stack(14,
            Way("IconCalendarLtr", "Where suggestions wait",
                "In the day's review. Open it from the notification area, a day's header in the list or calendar, or the card above the timer the next morning."),
            Way("IconAlert", "When the app interrupts",
                $"Only when the timer stops, and only for splits where the logs cover at least {(int)(SplitSuggestion.StrongCoverage * 100)}% of the time with {SplitSuggestion.StrongPrompts} or more prompts per project."),
            Way("IconSparkle", "Reading a suggestion", "Ticks are prompts, bars are agent attention, hatched is time the logs don't cover."));
        stack.Children.Add(ways.Margin(0, 20, 0, 0));
        return stack;
    }

    /// <summary>How the app behaves from now on, one way at a time.</summary>
    private static Control Way(string icon, string title, string caption)
    {
        var mark = new Border { Child = Ui.Icon(icon, "brand"), Width = 30, Height = 30, CornerRadius = new CornerRadius(8) }.Res(Border.BackgroundProperty, "BrandTintBrush");
        mark.Child!.HorizontalAlignment = HorizontalAlignment.Center;
        mark.Child.VerticalAlignment = VerticalAlignment.Center;
        mark.VerticalAlignment = VerticalAlignment.Top;
        return Ui.Columns("Auto,*", 12, mark, Ui.Stack(2, Ui.Text(title, "strong"), Ui.Wrapped(caption, "secondary")));
    }

    // Footer

    private Control Footer()
    {
        var dots = Ui.Row(6);
        foreach (var step in new[] { Step.Found, Step.Link, Step.Review })
        {
            var dot = new Border { Height = 6, Width = step == Current ? 18 : 6, CornerRadius = new CornerRadius(3) }.Res(Border.BackgroundProperty, step == Current ? "BrandBrush" : "DividerBrush");
            dots.Children.Add(dot.Centered());
        }
        dots.Spoken($"Step {(int)Current + 1} of 3");
        var buttons = Ui.Row(8);
        buttons.HorizontalAlignment = HorizontalAlignment.Right;
        switch (Current)
        {
            case Step.Found:
                buttons.Children.Add(Ui.Button("Close", () => owner.Dialogs.Close(null)));
                var hasRepositories = findings?.Repositories.Count > 0;
                var next = Ui.Button(hasRepositories ? "Next" : "Done", () =>
                {
                    if (hasRepositories) Go(Step.Link);
                    else owner.Dialogs.Close(null);
                }, "primary").Named("OnboardingNext");
                next.IsEnabled = findings is not null;
                buttons.Children.Add(next);
                break;
            case Step.Link:
                buttons.Children.Add(Ui.Button("Back", () => Go(Step.Found)));
                var changed = ChangedLinks();
                buttons.Children.Add(Ui.Button(changed.Count == 0 ? "Next" : changed.Count == 1 ? "Link 1 repository" : $"Link {changed.Count} repositories", async () =>
                {
                    foreach (var (repository, projectId) in changed) await owner.Splits.LinkAsync(repository, projectId);
                    Go(Step.Review);
                }, owner.Notify, "primary").Named("OnboardingLink"));
                break;
            case Step.Review:
                var count = owner.Desktop.Review(Week).Count;
                buttons.Children.Add(Ui.Button("Done", () => owner.Dialogs.Close(null)).Named("OnboardingDone"));
                if (count > 0)
                    buttons.Children.Add(Ui.Button($"Review last week ({count})", () => owner.Dialogs.Close(ReviewScope.LastSevenDays(today)), "primary").Named("OnboardingReview"));
                break;
        }
        return Ui.Columns("Auto,*", 12, dots.Centered(), buttons);
    }

    public void Go(Step step)
    {
        Current = step;
        Render();
    }

    /// <summary>The repositories whose project was chosen here, and differs from the person's own link.</summary>
    private List<(AgentRepository Repository, Guid ProjectId)> ChangedLinks() =>
        findings is null ? [] : findings.Repositories
            .Where(f => choices.GetValueOrDefault(f.Repository.Identity) is { } id && owner.Splits.Chosen(f.Repository) != id)
            .Select(f => (f.Repository, choices[f.Repository.Identity]!.Value)).ToList();
}
