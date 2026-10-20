using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Threading;

namespace Spindle.Windows.Views;

/// <summary>
/// Offers descriptions written before while one is typed in the entry editor, those on the project first. Unlike the
/// timer's field, picking one only fills in the text: the entry's project, task and split stay as they are. The list
/// opens as the person types (or presses Down), the arrow keys move through it, Return takes the highlighted one and
/// Escape closes it. As on the Mac and the web.
/// </summary>
public sealed class DescriptionCompletion
{
    private readonly MainWindow owner;
    private readonly TextBox box;
    private readonly Func<Guid?> projectId;
    private readonly Popup popup;
    private readonly StackPanel list = new() { Spacing = 1 };
    private readonly DispatcherTimer debounce = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private string[] options = [];
    private int highlighted = -1;
    private int lookup;
    // Set while the text is changed by picking, so that change does not open the list again.
    private bool picking;

    private DescriptionCompletion(MainWindow owner, TextBox box, Func<Guid?> projectId)
    {
        this.owner = owner;
        this.box = box;
        this.projectId = projectId;
        popup = new Popup
        {
            Child = new Border { Child = list, Padding = new Thickness(5) }.Classed("flyout"),
            PlacementTarget = box,
            Placement = PlacementMode.BottomEdgeAlignedLeft,
            VerticalOffset = 4,
            IsLightDismissEnabled = false,
        };
        box.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty && box.IsFocused && !picking) Restart();
        };
        box.LostFocus += (_, _) => DispatcherTimer.RunOnce(Close, TimeSpan.FromMilliseconds(200));
        box.AddHandler(InputElement.KeyDownEvent, OnKey, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        debounce.Tick += async (_, _) =>
        {
            debounce.Stop();
            await LookUpAsync();
        };
    }

    /// <summary>The box with its list of earlier descriptions, to put where the box would go.</summary>
    public static Control Attach(MainWindow owner, TextBox box, Func<Guid?> projectId)
    {
        var completion = new DescriptionCompletion(owner, box, projectId);
        return new Panel { Children = { box, completion.popup } };
    }

    private void Restart()
    {
        debounce.Stop();
        debounce.Start();
    }

    private async Task LookUpAsync()
    {
        var term = (box.Text ?? "").Trim();
        var ticket = ++lookup;
        string[] found;
        try
        {
            found = term.Length == 0 ? [] : await owner.Api!.GetDescriptionSuggestionsAsync(owner.WorkspaceId, term, projectId());
        }
        catch (Exception)
        {
            found = [];
        }
        if (ticket != lookup || !box.IsFocused) return;
        var now = (box.Text ?? "").Trim();
        options = found.Where(d => d.Contains(now, StringComparison.CurrentCultureIgnoreCase) && !d.Equals(now, StringComparison.CurrentCultureIgnoreCase)).ToArray();
        highlighted = -1;
        Render();
        popup.IsOpen = options.Length > 0;
    }

    private void Render()
    {
        list.Children.Clear();
        popup.Width = Math.Max(box.Bounds.Width, 240);
        for (var index = 0; index < options.Length; index++)
        {
            var text = options[index];
            var position = index;
            var row = new Button { Content = Ui.Text(text), Padding = new Thickness(8, 6) }.Classed("row");
            row.PointerEntered += (_, _) => Highlight(position);
            row.Click += (_, _) => Pick(text);
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

    private void Pick(string text)
    {
        Close();
        picking = true;
        box.Text = text;
        box.CaretIndex = text.Length;
        picking = false;
        box.Focus();
    }

    private void Close()
    {
        lookup++;
        debounce.Stop();
        popup.IsOpen = false;
        highlighted = -1;
    }

    private void OnKey(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down when options.Length > 0:
                popup.IsOpen = true;
                Highlight(Math.Min(highlighted + 1, options.Length - 1));
                break;
            case Key.Down when (box.Text ?? "").Trim().Length > 0:
                Restart();
                break;
            case Key.Up when popup.IsOpen && highlighted >= 0:
                Highlight(highlighted - 1);
                break;
            case Key.Escape when popup.IsOpen:
                Close();
                break;
            case Key.Enter when popup.IsOpen && highlighted >= 0 && highlighted < options.Length:
                Pick(options[highlighted]);
                break;
            default:
                return;
        }
        e.Handled = true;
    }
}
