using Avalonia;
using Avalonia.Threading;
using Spindle.Platform;
using Velopack;

namespace Spindle.Windows;

internal static class Program
{
    private static SingleInstance? instance;

    [STAThread]
    public static void Main(string[] args)
    {
        // Velopack's install, update and uninstall steps run here and exit; a normal launch carries on.
        var velopack = VelopackApp.Build();
        if (OperatingSystem.IsWindows()) velopack.OnBeforeUninstallFastCallback(_ => WindowsDesktop.SetLaunchAtLogin(false));
        velopack.Run();
        using var lifetime = new SingleInstance();
        if (!lifetime.IsFirstInstance) return;
        instance = lifetime;
        try { BuildAvaloniaApp().StartWithClassicDesktopLifetime(args); }
        finally { instance = null; }
    }

    /// <summary>Call after creating the main window; activation arrives from a background wait callback.</summary>
    public static void ListenForActivation(Action activate) =>
        instance?.ListenForActivation(() => Dispatcher.UIThread.Post(activate));

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
}
