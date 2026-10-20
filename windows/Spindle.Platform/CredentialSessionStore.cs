using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Spindle.Core;

namespace Spindle.Platform;

/// <summary>Refresh tokens belong to a single server and the signed-in Windows user.</summary>
public sealed class CredentialSessionStore : ISessionStore
{
    private readonly string target;
    public CredentialSessionStore(Uri serverUri)
    {
        var canonical = serverUri.AbsoluteUri.TrimEnd('/');
        target = "Spindle/" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    public Task<StoredSession?> LoadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureWindows();
        if (!CredRead(target, 1, 0, out var pointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == 1168) return Task.FromResult<StoredSession?>(null);
            throw new Win32Exception(error);
        }
        try
        {
            var credential = Marshal.PtrToStructure<Credential>(pointer);
            var bytes = new byte[credential.BlobSize];
            Marshal.Copy(credential.Blob, bytes, 0, bytes.Length);
            try { return Task.FromResult<StoredSession?>(new StoredSession(Encoding.UTF8.GetString(bytes))); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        finally { CredFree(pointer); }
    }

    public Task SaveAsync(StoredSession session, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureWindows();
        var bytes = Encoding.UTF8.GetBytes(session.RefreshToken);
        // Windows generic credentials have a 2560-byte blob limit.
        if (bytes.Length > 2560) throw new InvalidOperationException("The refresh token exceeds Windows Credential Manager capacity.");
        var pointer = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, pointer, bytes.Length);
            var credential = new Credential { Type = 1, Target = target, BlobSize = (uint)bytes.Length, Blob = pointer, Persist = 2, UserName = "Spindle" };
            if (!CredWrite(ref credential, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally
        {
            Marshal.Copy(new byte[bytes.Length], 0, pointer, bytes.Length);
            Marshal.FreeHGlobal(pointer);
            CryptographicOperations.ZeroMemory(bytes);
        }
        return Task.CompletedTask;
    }

    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureWindows();
        if (!CredDelete(target, 1, 0) && Marshal.GetLastWin32Error() != 1168) throw new Win32Exception(Marshal.GetLastWin32Error());
        return Task.CompletedTask;
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Secure sign-in requires Windows Credential Manager. Use demo mode to preview on other systems.");
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags;
        public uint Type;
        public string? Target;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint BlobSize;
        public IntPtr Blob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? Alias;
        public string? UserName;
    }
    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);
    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredWrite(ref Credential credential, uint flags);
    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredDelete(string target, uint type, uint flags);
    [DllImport("advapi32.dll")] private static extern void CredFree(IntPtr credential);
}

/// <summary>Ephemeral session storage for explicitly selected demo/test use; never writes tokens to disk.</summary>
public sealed class InMemorySessionStore : ISessionStore
{
    private StoredSession? session;
    public Task<StoredSession?> LoadAsync(CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(session); }
    public Task SaveAsync(StoredSession value, CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); session = value; return Task.CompletedTask; }
    public Task ClearAsync(CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); session = null; return Task.CompletedTask; }
}
