using Avalonia;
using Avalonia.Controls;
using Spindle.Windows.Views.Shell;

namespace Spindle.Windows.Views;

public sealed class NoWorkspaceView : UserControl
{
    public NoWorkspaceView(Action openWeb, Func<Task> signOut)
    {
        var open = Ui.Button("Open Spindle on the web", openWeb, "primary");
        var leave = Ui.Button("Sign out", signOut, _ => { });
        var stack = Ui.Stack(12, Ui.Logo(32), Ui.Text("No workspace yet", "subtitle"), Ui.Wrapped("Your account has no workspace. Open the web app to create or join one.", "secondary"), Ui.Row(8, open, leave));
        stack.MaxWidth = 420;
        stack.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center;
        stack.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
        Content = stack;
    }
}
