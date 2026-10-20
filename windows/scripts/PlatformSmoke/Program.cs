using Spindle.Core;
using Spindle.Platform;

var directory = Path.Combine(Path.GetTempPath(), "spindle-platform-smoke-" + Guid.NewGuid().ToString("N"));
try
{
    var store = new PreferencesStore(Path.Combine(directory, "settings.json"));
    Check(store.Load().ServerUrl == "https://app.spindletrack.com", "Default cloud address");
    var preferences = new DesktopPreferences { ServerUrl = "https://self-hosted.example.com", WorkspaceId = Guid.NewGuid(), AgentLogsEnabled = true };
    store.Save(preferences);
    Check(store.Load() == preferences, "Preferences round trip");
    var stated = preferences with { SplitStatements = new() { [Guid.NewGuid()] = new SplitStatement("a/b/50,c/d/50", DateTimeOffset.UnixEpoch.AddDays(20000), Notified: true) } };
    store.Save(stated);
    Check(store.Load() == stated, "Split statements round trip");
    store.Save(preferences with { AgentLogsEnabled = false });
    Check(!store.Load().AgentLogsEnabled, "Preferences replacement");
    File.WriteAllText(Path.Combine(directory, "settings.json"), "broken JSON");
    Check(store.Load() == new DesktopPreferences(), "Corrupt preferences recovery");
}
finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }

var demo = new InMemorySessionStore();
await demo.SaveAsync(new StoredSession("demo-refresh"));
Check((await demo.LoadAsync())?.RefreshToken == "demo-refresh", "Ephemeral demo session");
await demo.ClearAsync();
Check(await demo.LoadAsync() is null, "Demo sign-out");

if (OperatingSystem.IsWindows())
{
    var scope = Guid.NewGuid().ToString("N");
    var credentials = new CredentialSessionStore(new Uri($"https://{scope}.invalid"));
    var otherServer = new CredentialSessionStore(new Uri($"https://other-{scope}.invalid"));
    try
    {
        Check(await credentials.LoadAsync() is null, "No initial credential");
        await credentials.SaveAsync(new StoredSession("Windows smoke token π"));
        Check((await credentials.LoadAsync())?.RefreshToken == "Windows smoke token π", "Native credential round trip");
        Check(await otherServer.LoadAsync() is null, "Credentials isolated by server");
        await credentials.ClearAsync();
        Check(await credentials.LoadAsync() is null, "Native credential removal");
        await credentials.ClearAsync();
    }
    finally { await credentials.ClearAsync(); }

    using var activated = new ManualResetEventSlim();
    using var first = new SingleInstance("Spindle.Smoke." + scope);
    Check(first.IsFirstInstance, "First instance");
    first.ListenForActivation(() => activated.Set());
    using var second = new SingleInstance("Spindle.Smoke." + scope);
    Check(!second.IsFirstInstance && activated.Wait(TimeSpan.FromSeconds(5)), "Second instance activates first");
    using (var shortcut = new GlobalTimerShortcut(() => { }))
    {
        try
        {
            using var conflicting = new GlobalTimerShortcut(() => { });
            throw new Exception("A duplicate global shortcut registration must fail.");
        }
        catch (System.ComponentModel.Win32Exception) { Console.WriteLine("PASS: native shortcut conflict"); }
    }
    using (var shortcut = new GlobalTimerShortcut(() => { }))
        Console.WriteLine("PASS: native shortcut release and registration");
    Check(WindowsDesktop.GetIdleDuration() is { } idle && idle >= TimeSpan.Zero, "Native idle timestamp");
}
else
{
    try
    {
        await new CredentialSessionStore(new Uri("https://example.com")).LoadAsync();
        throw new Exception("Production sign-in must reject insecure storage on other platforms.");
    }
    catch (PlatformNotSupportedException) { Console.WriteLine("PASS: secure storage fails closed on other platforms"); }
}
Console.WriteLine("Platform smoke checks passed.");

static void Check(bool condition, string name)
{
    if (!condition) throw new Exception(name);
    Console.WriteLine("PASS: " + name);
}
