using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using Spindle.Core;

namespace Spindle.Windows.Views;

/// <summary>
/// The "What are you working on?" field. While typing it offers earlier entries; the arrow keys move through them,
/// Return starts the highlighted one or, with none highlighted, submits what was typed. As on the Mac.
/// </summary>
public sealed class DescriptionField : UserControl
{
    private readonly MainWindow owner;
    private readonly Popup popup;
    private readonly StackPanel list = new() { Spacing = 1 };
    private readonly DispatcherTimer debounce = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private TimeEntrySuggestion[] suggestions = [];
    private int highlighted = -1;
    private int lookup;

    public DescriptionField(MainWindow owner)
    {
        this.owner = owner;
        Box = new TextBox { PlaceholderText = "What are you working on?", MaxLength = 3000, Name = "TimerDescription" }.Classed("bare", "large");
        Box.VerticalAlignment = VerticalAlignment.Center;
        Box.TextChanged += (_, _) =>
        {
            highlighted = -1;
            if (Box.IsFocused) Restart();
        };
        Box.GotFocus += (_, _) => Restart();
        Box.LostFocus += (_, _) => DispatcherTimer.RunOnce(() => popup!.IsOpen = false, TimeSpan.FromMilliseconds(200));
        Box.KeyDown += OnKey;
        debounce.Tick += async (_, _) =>
        {
            debounce.Stop();
            await LookUpAsync();
        };
        var panel = new Border { Child = Ui.Stack(2, Ui.Text("Previous entries", "sectionHeader").Margin(8, 4, 0, 2), list), Padding = new Thickness(5), Width = 560 }.Classed("flyout");
        popup = new Popup
        {
            Child = panel,
            PlacementTarget = Box,
            Placement = PlacementMode.BottomEdgeAlignedLeft,
            VerticalOffset = 8,
            IsLightDismissEnabled = false,
        };
        Content = new Panel { Children = { Box, popup } };
    }

    public TextBox Box { get; }
    public string Text
    {
        get => Box.Text ?? "";
        set => Box.Text = value;
    }

    /// <summary>Return with nothing highlighted.</summary>
    public event Action? Submitted;

    /// <summary>An earlier entry was picked, to start again.</summary>
    public event Action<TimeEntrySuggestion>? Picked;

    private void Restart()
    {
        debounce.Stop();
        debounce.Start();
    }

    private async Task LookUpAsync()
    {
        var term = Text.Trim();
        var ticket = ++lookup;
        TimeEntrySuggestion[] found;
        try
        {
            found = await owner.Api!.GetTimeEntrySuggestionsAsync(owner.WorkspaceId, term, 8);
        }
        catch (Exception)
        {
            found = Recent(term);
        }
        if (ticket != lookup || !Box.IsFocused) return;
        suggestions = found.Where(s => term.Length == 0 || s.Description.Contains(term, StringComparison.CurrentCultureIgnoreCase)
            || owner.Catalog.Project(s.ProjectId)?.Name.Contains(term, StringComparison.CurrentCultureIgnoreCase) == true).Take(8).ToArray();
        Render();
        popup.IsOpen = suggestions.Length > 0;
    }

    /// <summary>Earlier entries on this PC, when the server can't be asked.</summary>
    private TimeEntrySuggestion[] Recent(string term) => owner.Entries
        .Where(e => e.Stop is not null && (term.Length == 0 || e.Description.Contains(term, StringComparison.CurrentCultureIgnoreCase)))
        .DistinctBy(e => (e.Description, e.ProjectId, e.TaskId))
        .Take(8)
        .Select(e => new TimeEntrySuggestion { Description = e.Description, ProjectId = e.ProjectId, TaskId = e.TaskId, TagIds = e.TagIds, IsBillable = e.IsBillable, Shares = e.Shares })
        .ToArray();

    private void Render()
    {
        list.Children.Clear();
        for (var index = 0; index < suggestions.Length; index++)
        {
            var suggestion = suggestions[index];
            var position = index;
            var description = Ui.Text(suggestion.Description.Length == 0 ? "No description" : suggestion.Description, suggestion.Description.Length == 0 ? "secondary" : "");
            Control? project = owner.Catalog.Project(suggestion.ProjectId) is { } found
                ? SplitViews.ProjectLabel(found, owner.Catalog.Task(suggestion.TaskId), owner.Catalog.Client(found.ClientId))
                : null;
            if (project is not null) project.HorizontalAlignment = HorizontalAlignment.Right;
            var row = new Button { Content = Ui.Columns("*,Auto", 12, description, project), Padding = new Thickness(8, 6) }.Classed("row");
            if (index == highlighted) row.Classes.Add("highlighted");
            row.PointerEntered += (_, _) => Highlight(position);
            row.Click += (_, _) => Pick(suggestion);
            list.Children.Add(row);
        }
    }

    private void Highlight(int index)
    {
        highlighted = index;
        for (var position = 0; position < list.Children.Count; position++)
        {
            if (position == index) list.Children[position].Classes.Add("highlighted");
            else list.Children[position].Classes.Remove("highlighted");
        }
    }

    private void Pick(TimeEntrySuggestion suggestion)
    {
        popup.IsOpen = false;
        highlighted = -1;
        Picked?.Invoke(suggestion);
    }

    private void OnKey(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                if (!popup.IsOpen && suggestions.Length > 0) popup.IsOpen = true;
                if (suggestions.Length > 0) Highlight(Math.Min(highlighted + 1, suggestions.Length - 1));
                break;
            case Key.Up:
                if (highlighted >= 0) Highlight(highlighted - 1);
                break;
            case Key.Escape:
                if (popup.IsOpen)
                {
                    popup.IsOpen = false;
                    highlighted = -1;
                }
                else
                {
                    TopLevel.GetTopLevel(this)?.Focus();
                }
                break;
            case Key.Enter:
                if (popup.IsOpen && highlighted >= 0 && highlighted < suggestions.Length)
                {
                    Pick(suggestions[highlighted]);
                }
                else
                {
                    popup.IsOpen = false;
                    Submitted?.Invoke();
                }
                break;
            default:
                return;
        }
        e.Handled = true;
    }
}
