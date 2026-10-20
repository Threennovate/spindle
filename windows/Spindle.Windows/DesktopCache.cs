using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Spindle.Core;
namespace Spindle.Windows;

internal sealed record DesktopSnapshot(MeResponse Me, ProjectResponse[] Projects, TagResponse[] Tags, Guid WorkspaceId,
    ClientResponse[]? Clients = null, WorkspaceSettingsResponse? Settings = null);
internal sealed class DesktopCache(Uri server, string dataDirectory)
{
    private readonly string path = Path.Combine(dataDirectory, "Accounts",
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(server.AbsoluteUri))), "desktop.json");
    public async Task<DesktopSnapshot?> LoadAsync()
    {
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<DesktopSnapshot>(await File.ReadAllTextAsync(path), SpindleApiClient.JsonOptions); }
        catch (JsonException) { return null; }
    }
    public async Task SaveAsync(DesktopSnapshot snapshot)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(snapshot, SpindleApiClient.JsonOptions)); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public void Clear() { if (File.Exists(path)) File.Delete(path); }
}
