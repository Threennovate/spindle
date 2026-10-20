using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;

namespace Spindle.Windows.Views.Shell;

public enum NoticeKind { Success, Error }

/// <summary>
/// A short message at the bottom of the window, e.g. that an entry was deleted or couldn't be saved: one at a time,
/// gone after a few seconds (longer for errors, or with a button such as "Split saved · Undo"), as on the Mac. Replaces
/// the old permanent status footer.
/// </summary>
public sealed class NoticeHost : Border
{
    private readonly DispatcherTimer hiding = new();
    private readonly TextBlock text = Ui.Wrapped("").Named("NoticeText");
    private readonly ContentControl icon = new();
    private readonly ContentControl action = new();

    public NoticeHost()
    {
        Classes.Add("toast");
        MaxWidth = 520;
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Bottom;
        Margin = new Thickness(20, 0, 20, 20);
        IsVisible = false;
        text.VerticalAlignment = VerticalAlignment.Center;
        var dismiss = Ui.IconButton("IconDismiss16", "Dismiss", Hide, "small");
        action.VerticalAlignment = VerticalAlignment.Center;
        Child = Ui.Columns("Auto,*,Auto,Auto", 10, icon, text, action, dismiss);
        hiding.Tick += (_, _) => Hide();
    }

    public string? Current => IsVisible ? text.Text : null;
    public NoticeKind Kind { get; private set; }

    /// <summary>The button's label, e.g. "Undo", while the notice has one.</summary>
    public string? Action => IsVisible ? (action.Content as Button)?.Content as string : null;

    /// <summary>Shows a message; <paramref name="button"/> adds a button that hides the notice when clicked.</summary>
    public void Show(string message, NoticeKind kind, (string Label, Func<Task> Action)? button = null)
    {
        Kind = kind;
        action.Content = button is { } b
            ? Ui.Button(b.Label, () => { Hide(); return b.Action(); }, _ => { }, "link").Named("NoticeAction")
            : null;
        text.Text = message;
        icon.Content = kind == NoticeKind.Error
            ? Ui.Icon("IconWarningFilled", "critical").Centered()
            : Ui.Icon("IconCheckmarkCircleFilled", "brand").Centered();
        IsVisible = true;
        hiding.Stop();
        hiding.Interval = TimeSpan.FromSeconds(button is not null ? 8 : kind == NoticeKind.Error ? 6 : 3);
        hiding.Start();
    }

    public void Hide()
    {
        hiding.Stop();
        IsVisible = false;
    }
}

/// <summary>
/// Dialogs inside the window, over a scrim, as Windows 11's content dialogs: confirmations, editors and choices.
/// One at a time; a second waits until the first closes.
/// </summary>
public sealed class DialogHost : Panel
{
    private readonly Border scrim = new Border().Res(Border.BackgroundProperty, "ScrimBrush");
    private readonly Border frame = new Border
    {
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(24),
        ClipToBounds = true,
    }.Classed("flyout").Named("Dialog");
    private readonly SemaphoreSlim gate = new(1, 1);
    private TaskCompletionSource<object?>? waiting;

    public DialogHost()
    {
        IsVisible = false;
        Children.Add(scrim);
        Children.Add(frame);
        frame.Res(Border.BackgroundProperty, "SolidCardBackgroundBrush");
    }

    /// <summary>What is shown now, for tests and for keyboard handling.</summary>
    public Control? Content => frame.Child as Control;

    public async Task<object?> ShowAsync(Control content, double width)
    {
        await gate.WaitAsync();
        try
        {
            waiting = new TaskCompletionSource<object?>();
            frame.Width = width;
            frame.Child = content;
            IsVisible = true;
            return await waiting.Task;
        }
        finally
        {
            frame.Child = null;
            IsVisible = false;
            waiting = null;
            gate.Release();
        }
    }

    /// <summary>
    /// Shows a dialog over the one open now, e.g. the branch rules from the entry editor, and brings that one back when it
    /// closes. With none open it is an ordinary dialog.
    /// </summary>
    public async Task<object?> ShowOverAsync(Control content, double width)
    {
        if (waiting is not { } under || frame.Child is not Control below) return await ShowAsync(content, width);
        var belowWidth = frame.Width;
        var over = new TaskCompletionSource<object?>();
        waiting = over;
        frame.Width = width;
        frame.Child = content;
        try
        {
            return await over.Task;
        }
        finally
        {
            frame.Width = belowWidth;
            frame.Child = below;
            waiting = under;
        }
    }

    public void Close(object? result) => waiting?.TrySetResult(result);

    /// <summary>A question with a confirming and a cancelling answer, the confirming one in brand or red.</summary>
    public async Task<bool> ConfirmAsync(string title, string message, string confirm = "Confirm", bool destructive = false, string cancel = "Cancel")
    {
        var yes = Ui.Button(confirm, () => Close(true), destructive ? "dangerFilled" : "primary").Named("ConfirmButton");
        var no = Ui.Button(cancel, () => Close(false)).Named("CancelButton");
        yes.MinWidth = 110;
        no.MinWidth = 110;
        var body = Ui.Stack(12, Ui.Text(title, "subtitle"), Ui.Wrapped(message));
        body.Margin = new Thickness(24, 24, 24, 24);
        var buttons = Ui.Row(8, yes, no);
        buttons.HorizontalAlignment = HorizontalAlignment.Right;
        var footer = new Border { Child = buttons, Padding = new Thickness(24, 20) }.Res(Border.BackgroundProperty, "CardBackgroundSecondaryBrush");
        var content = new DockPanel { Children = { footer.Dock(Avalonia.Controls.Dock.Bottom), body } };
        return await ShowAsync(content, 440) is true;
    }
}
