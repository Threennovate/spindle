using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Spindle.Windows.Model;
using Spindle.Windows.Views;

namespace Spindle.Windows;

public partial class App : Application
{
    private TrayIcon? tray;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var window = new MainWindow(dataDirectory: Environment.GetEnvironmentVariable("SPINDLE_DATA_DIRECTORY"));
            desktop.MainWindow = window;
            void Quit()
            {
                window.Quit();
                desktop.Shutdown();
            }
            void Show()
            {
                window.Show();
                window.WindowState = WindowState.Normal;
                window.Activate();
            }
            var quickEntry = new QuickEntryWindow(window);
            var flyout = new TrayFlyout(window, quickEntry.Open, Quit);
            window.QuickEntryRequested += quickEntry.Toggle;

            // Left click opens the panel; right click the menu, as notification-area icons do on Windows.
            var menu = new NativeMenu();
            var open = new NativeMenuItem("Open Spindle");
            open.Click += (_, _) => Show();
            var toggle = new NativeMenuItem("Start timer");
            toggle.Click += async (_, _) =>
            {
                try
                {
                    await window.ToggleTimerAsync();
                }
                catch (Exception ex)
                {
                    window.Notify(ex.Message);
                    Show();
                }
            };
            var quick = new NativeMenuItem("Quick entry…");
            quick.Click += (_, _) => quickEntry.Open();
            var quit = new NativeMenuItem("Quit Spindle");
            quit.Click += (_, _) => Quit();
            menu.Items.Add(open);
            menu.Items.Add(toggle);
            menu.Items.Add(quick);
            menu.Items.Add(new NativeMenuItemSeparator());
            menu.Items.Add(quit);
            tray = new TrayIcon { Icon = window.Icon, ToolTipText = "Spindle", Menu = menu, IsVisible = true };
            tray.Clicked += (_, _) => flyout.Toggle();
            TrayIcon.SetIcons(this, new TrayIcons { tray });
            window.TimerChanged += () =>
            {
                if (window.Running is { } running)
                {
                    var spark = window.Splits.Suggestion(running) is not null ? " ✦" : "";
                    tray.ToolTipText = $"Spindle · {Durations.Clock(running.Seconds(DateTimeOffset.UtcNow))}{spark}\n{(running.Description.Length == 0 ? "No description" : running.Description)}";
                }
                else
                {
                    tray.ToolTipText = "Spindle · No timer running";
                }
                toggle.Header = window.Running is null ? "Start timer" : "Stop timer";
            };
            window.Closed += (_, _) =>
            {
                tray.Dispose();
                desktop.Shutdown();
            };
            Program.ListenForActivation(() => Dispatcher.UIThread.Post(Show));
            desktop.Exit += (_, _) =>
            {
                tray?.Dispose();
                window.Updates.OnExit();
            };
        }
        base.OnFrameworkInitializationCompleted();
    }
}
