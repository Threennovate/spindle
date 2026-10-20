using System.Security.Cryptography;
using System.Text;

namespace Spindle.Activity;

/// <summary>Resolves worktrees to their common repository without executing git or exposing credentials.</summary>
public static class RepositoryResolver
{
    public static AgentRepository? Resolve(string cwd)
    {
        try
        {
            if (!Directory.Exists(cwd)) return null;
            var current = new DirectoryInfo(Path.GetFullPath(cwd));
            while (current is not null)
            {
                var git = Path.Combine(current.FullName, ".git");
                if (Directory.Exists(git) || File.Exists(git))
                {
                    var root = current.FullName;
                    var gitDirectory = git;
                    if (File.Exists(git))
                    {
                        var pointer = ReadSmall(git)?.Trim();
                        if (pointer?.StartsWith("gitdir:", StringComparison.Ordinal) != true) return null;
                        gitDirectory = Path.GetFullPath(pointer[7..].Trim(), root);
                        var common = ReadSmall(Path.Combine(gitDirectory, "commondir"));
                        if (common is not null)
                        {
                            gitDirectory = Path.GetFullPath(common.Trim(), gitDirectory);
                            root = Path.GetFileName(gitDirectory) == ".git"
                                ? Path.GetDirectoryName(gitDirectory)! : gitDirectory;
                        }
                    }
                    var origin = Origin(ReadSmall(Path.Combine(gitDirectory, "config")) ?? "");
                    return new AgentRepository(OriginIdentity(origin) ?? PathIdentity(root), Path.GetFileName(root));
                }
                current = current.Parent;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { }
        return null;
    }

    public static string PathIdentity(string path) => "path:" + Convert.ToHexStringLower(
        SHA256.HashData(Encoding.UTF8.GetBytes(path)))[..32];

    public static string? OriginIdentity(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var rest = value.Trim();
        string host, path;
        var scheme = rest.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0)
        {
            if (rest[..scheme].Equals("file", StringComparison.OrdinalIgnoreCase)) return null;
            rest = rest[(scheme + 3)..];
            var slash = rest.IndexOf('/');
            if (slash < 0) return null;
            var authority = rest[..slash].Split('@')[^1];
            host = authority.Split(':')[0];
            path = rest[slash..];
        }
        else
        {
            var slash = rest.IndexOf('/');
            var head = slash < 0 ? rest : rest[..slash];
            var at = head.LastIndexOf('@');
            if (at >= 0) rest = rest[(at + 1)..];
            var colon = rest.IndexOf(':');
            if (colon <= 1 || rest[..colon].Contains('/') || rest[..colon].Contains('\\')) return null;
            host = rest[..colon];
            path = rest[(colon + 1)..];
        }
        path = path.Split('?', '#')[0].Trim('/');
        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) path = path[..^4];
        var identity = $"origin:{host}/{path}".ToLowerInvariant();
        return host.Length == 0 || path.Length == 0 || identity.Length > 300 || identity.Any(char.IsWhiteSpace) ? null : identity;
    }

    private static string? ReadSmall(string path) => File.Exists(path) && new FileInfo(path).Length <= 1024 * 1024
        ? File.ReadAllText(path) : null;

    private static string? Origin(string config)
    {
        var inside = false;
        foreach (var line in config.Split('\n'))
        {
            var text = line.Trim();
            if (text.StartsWith('[')) inside = text == "[remote \"origin\"]";
            else if (inside && text.IndexOf('=') is var equals && equals > 0 && text[..equals].Trim() == "url")
                return text[(equals + 1)..].Trim();
        }
        return null;
    }
}
