using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Spindle.Activity;
using Spindle.Windows.Model;

namespace Spindle.Windows.Views;

/// <summary>
/// "Different projects by branch": rules that send a repository's work on some branches to another project, and task,
/// while the rest goes to the repository's own project. They go with the repository's link to the person's account, so
/// every device and the web place the work alike. Nothing already tracked changes: they only decide where suggestions
/// put the time. As the Mac's BranchRulesEditor.
/// </summary>
public sealed class BranchRulesEditor : UserControl
{
    /// <summary>A rule as it is being written.</summary>
    public sealed class Draft
    {
        public string Pattern { get; set; } = "";
        public Guid? ProjectId { get; set; }
        public Guid? TaskId { get; set; }
        public string Trimmed => Pattern.Trim();
    }

    private readonly MainWindow owner;
    private readonly AgentRepository repository;
    private readonly List<Draft> drafts;
    private readonly ContentControl recent = new();
    private readonly TextBlock said = Ui.Text("", "secondary").Named("BranchRulesProblem");
    private Button save = new();
    private Guid? projectId;
    private Draft? focusing;

    /// <param name="project">The project for the other branches, when one was chosen but isn't linked yet, e.g. in the onboarding.</param>
    /// <param name="rules">Rules to open on instead of the repository's own, e.g. to show how it looks with some.</param>
    public BranchRulesEditor(MainWindow owner, AgentRepository repository, Guid? project = null, IReadOnlyList<BranchRule>? rules = null)
    {
        this.owner = owner;
        this.repository = repository;
        Name = "BranchRulesEditor";
        projectId = project ?? owner.Splits.ProjectOf(repository);
        var existing = rules ?? owner.Splits.Rules(repository);
        drafts = existing.Count == 0 ? [new Draft()] : existing.Select(r => new Draft { Pattern = r.Pattern, ProjectId = r.ProjectId, TaskId = r.TaskId }).ToList();
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) owner.Dialogs.Close(null);
            else if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control) && Problem() is null) _ = SaveAsync();
            else return;
            e.Handled = true;
        };
        Render();
    }

    /// <summary>The rules as written now, for tests.</summary>
    public IReadOnlyList<Draft> Drafts => drafts;

    /// <summary>Opens the editor over whatever dialog is open, e.g. the entry editor or the onboarding.</summary>
    public static Task<object?> ShowAsync(MainWindow owner, AgentRepository repository, Guid? project = null) =>
        owner.Dialogs.ShowOverAsync(new BranchRulesEditor(owner, repository, project), 580);

    /// <summary>The rules to save: those with a pattern.</summary>
    private List<Draft> Written => drafts.Where(d => d.Trimmed.Length > 0).ToList();

    /// <summary>Why it can't be saved yet; null when it can.</summary>
    public string? Problem()
    {
        if (projectId is null) return "Choose the project for the other branches.";
        var written = Written;
        if (written.Any(d => d.ProjectId is null)) return "Choose a project for each rule.";
        var patterns = written.Select(d => d.Trimmed.ToLowerInvariant()).ToList();
        if (patterns.Distinct().Count() != patterns.Count) return "Each pattern can have one rule.";
        if (written.Any(d => d.Trimmed.Length > BranchRule.PatternMaxLength)) return "A pattern can be at most 255 characters.";
        return null;
    }

    private void Render()
    {
        // The parts kept between renders leave the old layout first.
        foreach (var kept in new Control[] { recent, said })
            if (kept.Parent is Panel holder) holder.Children.Remove(kept);
        var title = Ui.Text("Different projects by branch", "subtitle");
        var name = Ui.Row(8, Ui.Text(repository.Name, "medium"));
        if (repository.Identity.StartsWith("origin:", StringComparison.Ordinal))
        {
            var origin = Ui.Text(repository.Identity["origin:".Length..], "caption", "tertiary").Centered();
            ((TextBlock)origin).TextTrimming = TextTrimming.CharacterEllipsis;
            origin.MaxWidth = 320;
            name.Children.Add(origin);
        }
        var about = Ui.Wrapped("Work on branches that match a rule goes to its project. A session counts when all of its branches match; the first rule that matches wins. Use * for any characters, as in acme/* or ACME-*.", "secondary");
        var rules = Ui.Stack(8);
        foreach (var draft in drafts) rules.Children.Add(RuleRow(draft));
        var add = Ui.Button(Ui.Row(5, Ui.Icon("IconAdd", "small").Centered(), Ui.Text("Add rule").Centered()), () =>
        {
            var draft = new Draft();
            drafts.Add(draft);
            focusing = draft;
            Render();
        }, "link").Named("BranchRuleAdd");
        add.HorizontalAlignment = HorizontalAlignment.Left;
        add.IsEnabled = drafts.Count < BranchRule.MaxRules;
        rules.Children.Add(add);
        rules.Children.Add(Ui.Divider().Margin(0, 4, 0, 4));
        var otherLabel = Ui.Text("Other branches", "secondary").Centered();
        otherLabel.Width = PatternWidth;
        rules.Children.Add(Ui.Row(10, otherLabel, Arrow(), ProjectButton(projectId, null, tasks: false, (project, _) => projectId = project).Named("BranchRulesOther")));
        var body = Ui.Stack(0, title, name.Margin(0, 4, 0, 0), about.Margin(0, 10, 0, 0), rules.Margin(0, 16, 0, 0), recent);
        body.Margin = new Thickness(28, 24, 28, 20);

        save = Ui.Button("Save", SaveAsync, owner.Notify, "primary").Named("BranchRulesSave");
        save.MinWidth = 100;
        var cancel = Ui.Button("Cancel", () => owner.Dialogs.Close(null));
        cancel.MinWidth = 100;
        // Only a repository linked to a project has a link to take away.
        Control? unlink = owner.Splits.Chosen(repository) is null ? null : Ui.Button("Unlink Repository", async () =>
        {
            await RepositoryLinkButton.UnlinkAsync(owner, repository);
            owner.Dialogs.Close(true);
        }, owner.Notify, "danger").Named("BranchRulesUnlink").Tip($"{repository.Name}'s work goes to no project, and its branch rules go");
        var footer = new Border { Child = Ui.Columns("Auto,*,Auto,Auto", 8, unlink, said.Centered().Margin(unlink is null ? 0 : 8, 0, 0, 0), cancel, save), Padding = new Thickness(24, 14) }
            .Res(Border.BackgroundProperty, "CardBackgroundSecondaryBrush");
        Content = new DockPanel { Children = { footer.Dock(Dock.Bottom), Ui.Divider().Dock(Dock.Bottom), new ScrollViewer { Content = body, MaxHeight = 620 } } };
        Refresh();
    }

    private const double PatternWidth = 170;

    private static Control Arrow() => Ui.Text("→", "tertiary").Centered();

    private Control RuleRow(Draft draft)
    {
        var pattern = new TextBox { Text = draft.Pattern, PlaceholderText = "acme/*", Width = PatternWidth, Name = "BranchRulePattern" }.Classed("mono").Spoken("Branches");
        pattern.TextChanged += (_, _) =>
        {
            draft.Pattern = pattern.Text ?? "";
            // The recent branches follow the pattern as it is typed.
            Refresh();
        };
        if (focusing == draft)
        {
            focusing = null;
            pattern.AttachedToVisualTree += (_, _) => pattern.Focus();
        }
        var project = ProjectButton(draft.ProjectId, draft.TaskId, tasks: true, (project, task) => (draft.ProjectId, draft.TaskId) = (project, task)).Named("BranchRuleProject");
        var remove = Ui.IconButton("IconDismiss", $"Remove the rule for {(draft.Trimmed.Length == 0 ? "these branches" : draft.Trimmed)}", () =>
        {
            drafts.Remove(draft);
            if (drafts.Count == 0) drafts.Add(new Draft());
            Render();
        }, "subtle").Tip("Remove this rule");
        return Ui.Columns("Auto,Auto,*,Auto", 10, pattern, Arrow(), project, remove.Centered());
    }

    /// <summary>Redraws what depends on the patterns, without rebuilding the field being typed in.</summary>
    private void Refresh()
    {
        recent.Content = Recent();
        var problem = Problem();
        save.IsEnabled = problem is null;
        said.Text = problem is not null && (Written.Count > 0 || projectId is null) ? problem : "";
    }

    /// <summary>The project (and task) a rule sends work to, chosen in the usual picker.</summary>
    private Button ProjectButton(Guid? project, Guid? task, bool tasks, Action<Guid?, Guid?> onChange)
    {
        var catalog = owner.Catalog;
        Control label = catalog.Project(project) is { } chosen
            ? Ui.Row(6, Ui.Dot(chosen.Color, 8).Centered(), Ui.Text(chosen.Name).Centered(), catalog.Task(task) is { } named ? Ui.Text("· " + named.Name, "secondary").Centered() : null)
            : Ui.Text("Choose project", "secondary");
        var button = new Button
        {
            Content = Ui.Columns("*,Auto", 6, label, Ui.Icon("IconChevronUpDown", "tertiary", "small").Centered()),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            MinWidth = 200,
            Padding = new Thickness(10, 5, 10, 6),
        }.Spoken(catalog.Project(project)?.Name ?? "Choose project");
        button.Click += (_, _) => PickerList.Show(button, ProjectPicker.Create(owner, project, task, (picked, pickedTask) =>
            {
                // "No project" is no rule; the other branches need one too.
                if (picked is not null) onChange(picked, tasks ? pickedTask : null);
                Render();
                return Task.CompletedTask;
            }, taken: (p, t) => p is null || (!tasks && t is not null),
            placeholder: tasks ? "Which project and task are these branches for?" : "Which project are the other branches for?"));
        return button;
    }

    /// <summary>The branches agents worked on here lately, and where each would go with the rules as written.</summary>
    private Control? Recent()
    {
        var branches = owner.Splits.Branches(repository);
        if (branches.Count == 0) return null;
        var catalog = owner.Catalog;
        var written = Written;
        var list = Ui.Stack(6, Ui.Text("Recent branches", "caption", "strong", "secondary"));
        foreach (var branch in branches.Take(6))
        {
            var rule = written.FirstOrDefault(d => d.ProjectId is not null && BranchRule.Match(d.Trimmed, branch));
            var name = Ui.Text(branch, "mono");
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            name.MaxWidth = 220;
            var row = Ui.Row(6, name.Centered(), Arrow());
            if (catalog.Project(rule?.ProjectId ?? projectId) is { } project)
            {
                row.Children.Add(Ui.Dot(project.Color, 8).Centered());
                row.Children.Add(Ui.Text(project.Name).Centered());
                if (catalog.Task(rule?.TaskId) is { } task) row.Children.Add(Ui.Text("· " + task.Name, "secondary").Centered());
            }
            else row.Children.Add(Ui.Text("No project", "secondary").Centered());
            if (rule is not null) row.Children.Add(Ui.Text("rule", "caption", "strong", "brand").Centered());
            row.Spoken($"{branch} goes to {catalog.Project(rule?.ProjectId ?? projectId)?.Name ?? "no project"}{(rule is not null ? " by a rule" : "")}");
            list.Children.Add(row.Named("BranchRulesBranch"));
        }
        return new Border { Child = list, Padding = new Thickness(12), CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 18, 0, 0) }
            .Res(Border.BackgroundProperty, "SubtleFillHoverBrush").Named("BranchRulesRecent");
    }

    public async Task SaveAsync()
    {
        if (projectId is not { } own || Problem() is not null) return;
        await owner.Splits.LinkAsync(repository, own, Written.Select(d => new BranchRule(d.Trimmed, d.ProjectId!.Value, d.TaskId)).ToList());
        owner.Dialogs.Close(true);
    }
}

/// <summary>"Different projects by branch…", for a repository that has, or is about to have, a project.</summary>
internal static class BranchRulesButton
{
    /// <param name="short">"Branches…" where room is short.</param>
    /// <param name="project">The project chosen for the repository, if it isn't linked yet.</param>
    public static Button Create(MainWindow owner, AgentRepository repository, bool @short = false, Action? saved = null, Guid? project = null)
    {
        var count = owner.Splits.Rules(repository).Count;
        var text = @short ? count > 0 ? $"Branches ({count})…" : "Branches…" : count > 0 ? $"Different projects by branch ({count})…" : "Different projects by branch…";
        var button = Ui.Button(Ui.Row(4, Ui.Icon("IconBranch", "small").Centered(), Ui.Text(text, "caption").Centered()), async () =>
        {
            if (await BranchRulesEditor.ShowAsync(owner, repository, project) is true) saved?.Invoke();
        }, owner.Notify, "link").Named("BranchRules")
            .Tip($"Send the work on some branches of {repository.Name} to another project, e.g. acme/* to the client's project");
        return button;
    }
}
