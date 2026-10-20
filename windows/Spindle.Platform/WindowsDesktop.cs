using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Spindle.Platform;

public static class WindowsDesktop
{
    public static bool IsLaunchAtLoginEnabled()
    {
        if (!OperatingSystem.IsWindows()) return false;
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        return key?.GetValue("Spindle") is string;
    }

    public static void SetLaunchAtLogin(bool enabled)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Launch at login is available on Windows.");
        SetLaunchAtLoginWindows(enabled);
    }

    [SupportedOSPlatform("windows")]
    private static void SetLaunchAtLoginWindows(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (enabled)
        {
            var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot locate the Spindle executable.");
            if (!Path.GetFileNameWithoutExtension(executable).Equals("Spindle", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Launch at login requires the installed Spindle executable.");
            key.SetValue("Spindle", $"\"{executable}\" --background", RegistryValueKind.String);
        }
        else key.DeleteValue("Spindle", throwOnMissingValue: false);
    }

    /// <summary>Elapsed time since keyboard or pointer input, without reading apps or windows.</summary>
    public static TimeSpan? GetIdleDuration()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref info)) return null;
        return TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.Tick));
    }

    [StructLayout(LayoutKind.Sequential)] private struct LastInputInfo { public uint Size; public uint Tick; }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetLastInputInfo(ref LastInputInfo info);
}

/// <summary>One process per logged-in Windows session, with a signal to reopen the first window.</summary>
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex mutex;
    private readonly EventWaitHandle? activation;
    private RegisteredWaitHandle? registration;
    public bool IsFirstInstance { get; }
    public SingleInstance(string name = "Spindle.Windows")
    {
        if (!OperatingSystem.IsWindows()) { mutex = new Mutex(false); IsFirstInstance = true; return; }
        mutex = new Mutex(false, @"Local\" + name, out var created);
        IsFirstInstance = created;
        activation = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\" + name + ".Activate");
        if (!created) activation.Set();
    }
    public void ListenForActivation(Action activate)
    {
        if (!IsFirstInstance || activation is null) return;
        registration = ThreadPool.RegisterWaitForSingleObject(activation, (_, timedOut) => { if (!timedOut) activate(); }, null, Timeout.Infinite, executeOnlyOnce: false);
    }
    public void Dispose() { registration?.Unregister(null); activation?.Dispose(); mutex.Dispose(); }
}

/// <summary>The Ctrl+Alt+Space shortcut alone, as earlier builds registered it. The caller marshals the callback.</summary>
public sealed class GlobalTimerShortcut : IDisposable
{
    private readonly GlobalHotKeys keys;

    public GlobalTimerShortcut(Action onPressed)
    {
        keys = new GlobalHotKeys([(1, GlobalHotKeys.Control | GlobalHotKeys.Alt, 0x20)], _ => onPressed());
        if (keys.Refused.Count > 0)
        {
            keys.Dispose();
            throw new Win32Exception("Ctrl+Alt+Space is already registered by another application.");
        }
    }

    public void Dispose() => keys.Dispose();
}

/// <summary>
/// System-wide shortcuts, each with an ID, on a thread of their own. Shortcuts another app holds are listed in
/// <see cref="Refused"/> instead of failing the rest. The caller marshals the callback to its UI thread.
/// </summary>
public sealed class GlobalHotKeys : IDisposable
{
    public const uint Alt = 0x0001;
    public const uint Control = 0x0002;
    public const uint Shift = 0x0004;
    public const uint Windows = 0x0008;
    private const uint NoRepeat = 0x4000;
    private const uint Quit = 0x0012;
    private readonly Thread thread;
    private readonly ManualResetEventSlim ready = new(false);
    private readonly List<int> refused = [];
    private uint threadId;
    private volatile bool disposed;

    public GlobalHotKeys(IReadOnlyList<(int Id, uint Modifiers, uint Key)> shortcuts, Action<int> onPressed)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Global shortcuts are available on Windows.");
        thread = new Thread(() => Run(shortcuts, onPressed)) { IsBackground = true, Name = "Spindle global shortcuts" };
        thread.Start();
        ready.Wait();
    }

    /// <summary>The IDs of shortcuts another application already holds.</summary>
    public IReadOnlyList<int> Refused => refused;

    private void Run(IReadOnlyList<(int Id, uint Modifiers, uint Key)> shortcuts, Action<int> onPressed)
    {
        threadId = GetCurrentThreadId();
        PeekMessage(out _, IntPtr.Zero, 0, 0, 0); // Create the thread's message queue before publishing readiness.
        var registered = new List<int>();
        foreach (var (id, modifiers, key) in shortcuts)
        {
            if (RegisterHotKey(IntPtr.Zero, id, modifiers | NoRepeat, key)) registered.Add(id);
            else refused.Add(id);
        }
        ready.Set();
        try
        {
            while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
                if (!disposed && message.Id == 0x0312) onPressed((int)message.WParam);
        }
        finally
        {
            foreach (var id in registered) UnregisterHotKey(IntPtr.Zero, id);
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        PostThreadMessage(threadId, Quit, UIntPtr.Zero, IntPtr.Zero);
        if (Thread.CurrentThread != thread) thread.Join();
        ready.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)] private struct Message { public IntPtr Window; public uint Id; public UIntPtr WParam; public IntPtr LParam; public uint Time; public int X; public int Y; public uint Private; }
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnregisterHotKey(IntPtr window, int id);
    [DllImport("user32.dll")] private static extern int GetMessage(out Message message, IntPtr window, uint min, uint max);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PeekMessage(out Message message, IntPtr window, uint min, uint max, uint remove);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PostThreadMessage(uint id, uint message, UIntPtr wParam, IntPtr lParam);
}
