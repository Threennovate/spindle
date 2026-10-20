using System.Reflection;
using Avalonia.Threading;
using Velopack;

namespace Spindle.Windows.Desktop;

/// <summary>
/// Updates for an installed Spindle, as Sparkle does on the Mac: a look at the feed soon after launch and every few
/// hours, the new version downloaded in the background, and installed when Spindle quits or when the person restarts
/// it from Settings. Development and unzipped builds aren't installed, so they don't update themselves.
/// </summary>
public sealed class AppUpdates
{
    private static readonly TimeSpan FirstLook = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Every = TimeSpan.FromHours(6);
    private readonly UpdateManager? manager;
    private readonly DispatcherTimer timer = new() { Interval = FirstLook };
    private VelopackAsset? pending;
    private bool restartAfterQuit;

    public AppUpdates()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var manager = new UpdateManager(Feed);
            if (manager.IsInstalled) this.manager = manager;
        }
        catch (Exception)
        {
            // An unusual install (or a missing Update.exe) keeps the app running, only without updates.
        }
        timer.Tick += async (_, _) =>
        {
            timer.Interval = Every;
            await CheckAsync();
        };
    }

    /// <summary>The feed this build was made for, from the Windows version settings.</summary>
    public static string Feed { get; } = typeof(AppUpdates).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(attribute => attribute.Key == "SpindleUpdatesUrl")?.Value ?? "https://app.spindletrack.com/downloads/spindle-windows";

    public static string Version { get; } =
        (Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "development build").Split('+')[0];

    public bool IsInstalled => manager is not null;
    public bool IsChecking { get; private set; }
    /// <summary>The version downloaded and waiting for a restart.</summary>
    public string? Ready => (pending ?? manager?.UpdatePendingRestart)?.Version.ToString();
    public string? LastError { get; private set; }
    public DateTimeOffset? LastChecked { get; private set; }
    public event Action? Changed;

    public void Start()
    {
        if (manager is not null) timer.Start();
    }

    /// <summary>Looks at the feed and downloads a newer version. Quiet on failure: the next look tries again.</summary>
    public async Task CheckAsync()
    {
        if (manager is null || IsChecking) return;
        IsChecking = true;
        LastError = null;
        Changed?.Invoke();
        try
        {
            if (await manager.CheckForUpdatesAsync() is { } update)
            {
                await manager.DownloadUpdatesAsync(update);
                pending = update.TargetFullRelease;
            }
            LastChecked = DateTimeOffset.Now;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
        }
        finally
        {
            IsChecking = false;
            Changed?.Invoke();
        }
    }

    /// <summary>Installs the waiting version once Spindle has quit, and opens it again.</summary>
    public void RestartAfterQuit() => restartAfterQuit = true;

    /// <summary>Call as Spindle exits: a downloaded version is installed after the process ends.</summary>
    public void OnExit()
    {
        timer.Stop();
        if (manager is null || (pending ?? manager.UpdatePendingRestart) is not { } update) return;
        try
        {
            manager.WaitExitThenApplyUpdates(update, silent: true, restart: restartAfterQuit);
        }
        catch (Exception)
        {
            // Velopack applies a downloaded version at the next launch anyway.
        }
    }
}
