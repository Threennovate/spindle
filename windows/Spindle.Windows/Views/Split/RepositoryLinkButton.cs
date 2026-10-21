using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Spindle.Activity;
using Spindle.Windows.Views.Shell;

namespace Spindle.Windows.Views;

/// <summary>
/// The project a repository's work goes to, as a button that changes it in the usual picker. "No project" there unlinks
/// it: neither a learned link nor a project named like it takes its work until a project is chosen again.
/// </summary>
internal static class RepositoryLinkButton
{
    public static Button Create(MainWindow owner, AgentRepository repository, Guid? projectId, Action changed)
    {
        var catalog = owner.Catalog;
        Control label = catalog.Project(projectId) is { } project
            ? Ui.Row(6, Ui.Dot(project.Color, 8).Centered(), Ui.Text(project.Name).Centered())
            : Ui.Text("No project", "secondary");
        var button = new Button
        {
            Content = Ui.Columns("*,Auto", 6, label, Ui.Icon("IconChevronUpDown", "tertiary", "small").Centered()),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            MinWidth = 180,
            Padding = new Thickness(10, 5, 10, 6),
        }.Named("RepositoryProject").Tip($"Where {repository.Name}'s agent work goes. Choose No project to unlink it.")
            .Spoken($"{repository.Name}: {catalog.Project(projectId)?.Name ?? "no project"}");
        button.Click += (_, _) => PickerList.Show(button, ProjectPicker.Create(owner, projectId, null, async (picked, _) =>
            {
                if (picked is { } chosen)
                {
                    if (chosen != projectId) await owner.Splits.LinkAsync(repository, chosen);
                }
                else if (!owner.Splits.IsNoProject(repository)) await UnlinkAsync(owner, repository);
                changed();
            }, taken: (_, task) => task is not null, placeholder: $"Which project is {repository.Name} for?",
            onIgnore: async () =>
            {
                await IgnoreAsync(owner, repository);
                changed();
            }));
        return button;
    }

    /// <summary>
    /// Ignores a repository on every device, with Undo, which brings back its link too: none of its agent work shows
    /// in Spindle or is uploaded, and the account keeps only its hash.
    /// </summary>
    public static async Task IgnoreAsync(MainWindow owner, AgentRepository repository)
    {
        var before = owner.Splits.ChoiceOf(repository);
        if (await owner.Splits.IgnoreAsync(repository) is not { } hash) return;
        async Task Undo()
        {
            await owner.Splits.UnignoreAsync(hash);
            if (before != Model.RepositoryChoice.Forgotten) await owner.Splits.ChooseAsync(repository, before);
        }
        owner.Notices.Show($"Ignored {repository.Name}", NoticeKind.Success, ("Undo", Undo));
    }

    /// <summary>Unlinks a repository, with Undo putting back what it had: its project and branch rules, or nothing chosen.</summary>
    public static async Task UnlinkAsync(MainWindow owner, AgentRepository repository)
    {
        var before = owner.Splits.ChoiceOf(repository);
        await owner.Splits.UnlinkAsync(repository);
        owner.Notices.Show($"Unlinked {repository.Name}", NoticeKind.Success, ("Undo", () => owner.Splits.ChooseAsync(repository, before)));
    }
}
