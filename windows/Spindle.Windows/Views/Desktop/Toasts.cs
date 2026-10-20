using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;

namespace Spindle.Windows.Views;

/// <summary>What a notification is about; a new one of a kind replaces the last, as on the Mac.</summary>
public enum ToastKind { Reminder, FocusDone, BreakOver, SplitOffer, SwitchOffer, QuickEntry, Proposals, SplitSaved, DayReview }

/// <summary>
/// Notifications in the corner of the screen, in the style of Windows 11's: a title, a line, and up to two actions,
/// gone after a while unless the pointer rests on them. Drawn by the app, so they work without a packaged identity.
/// </summary>
public static class Toasts
{
    private static readonly Dictionary<ToastKind, Window> shown = [];

    /// <summary>The toasts on screen, for tests.</summary>
    public static IReadOnlyDictionary<ToastKind, Window> Shown => shown;

    public static void Show(ToastKind kind, string title, string body, IReadOnlyList<(string Label, Func<Task> Action)> actions, Func<Task>? onClick = null)
    {
        // Toasts of an earlier UI thread (in tests, an earlier session) are gone with it.
        foreach (var stale in shown.Where(s => !s.Value.CheckAccess()).Select(s => s.Key).ToArray()) shown.Remove(stale);
        Close(kind);
        var window = new Window
        {
            Width = 364,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            Topmost = true,
            ShowActivated = false,
            WindowDecorations = WindowDecorations.None,
            Title = title,
        };
        window.Res(Window.BackgroundProperty, "FlyoutBackgroundBrush");
        var close = Ui.IconButton("IconDismiss16", "Dismiss", () => Close(kind), "small");
        var header = Ui.Columns("Auto,*,Auto", 8, Ui.LogoMark(16).Centered(), Ui.Text("Spindle", "caption", "secondary").Centered(), close);
        var text = Ui.Stack(4, Ui.Text(title, "strong"), Ui.Wrapped(body, "secondary"));
        var buttons = new Grid { ColumnSpacing = 8 };
        for (var index = 0; index < actions.Count; index++)
        {
            var (label, action) = actions[index];
            buttons.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
            var button = Ui.Button(label, async () =>
            {
                Close(kind);
                await action();
            }, _ => { }, index == 0 ? "primary" : "");
            button.HorizontalAlignment = HorizontalAlignment.Stretch;
            buttons.Children.Add(button.Column(index));
        }
        var content = Ui.Stack(12, header, text, actions.Count > 0 ? buttons : null);
        content.Margin = new Thickness(16, 12, 16, 16);
        window.Content = new Border { Child = content, BorderThickness = new Thickness(1) }.Res(Border.BorderBrushProperty, "FlyoutStrokeBrush");
        if (onClick is not null)
        {
            text.Cursor = new Cursor(StandardCursorType.Hand);
            text.PointerReleased += async (_, _) =>
            {
                Close(kind);
                await onClick();
            };
        }
        var hiding = new DispatcherTimer { Interval = TimeSpan.FromSeconds(kind is ToastKind.QuickEntry ? 6 : 10) };
        hiding.Tick += (_, _) =>
        {
            if (window.IsPointerOver) return;
            hiding.Stop();
            Close(kind);
        };
        window.Opened += (_, _) =>
        {
            if (window.Screens.Primary is { } screen)
            {
                var area = screen.WorkingArea;
                var scale = screen.Scaling;
                var height = (int)(window.Bounds.Height * scale);
                window.Position = new PixelPoint(area.Right - (int)(376 * scale), area.Bottom - height - (int)(12 * scale) - shown.Count(s => s.Value != window) * (height + (int)(8 * scale)));
            }
            hiding.Start();
        };
        window.Closed += (_, _) => hiding.Stop();
        shown[kind] = window;
        window.Show();
    }

    public static void Close(ToastKind kind)
    {
        if (!shown.Remove(kind, out var window)) return;
        // One of an earlier UI thread (in tests, an earlier session) is gone with it.
        if (window.CheckAccess()) window.Close();
    }
}
