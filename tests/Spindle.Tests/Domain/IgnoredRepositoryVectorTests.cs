using System.Text.Json;
using Spindle.Domain.AgentActivity;

namespace Spindle.Tests.Domain;

/// <summary>
/// How an ignored repository is named on the server (<c>tests/vectors/ignored-repositories.json</c>): the Mac and Windows
/// hash it the same way, so the list syncs without the server learning which repositories they are.
/// </summary>
public sealed class IgnoredRepositoryVectorTests
{
    private static readonly JsonElement Vectors = Load();

    public static TheoryData<string, string, string> Cases() => new(Vectors.GetProperty("cases").EnumerateArray()
        .Select(c => (c.GetProperty("account").GetString()!, c.GetProperty("identity").GetString()!, c.GetProperty("hash").GetString()!)));

    [Theory]
    [MemberData(nameof(Cases))]
    public void Hashes_a_repository_as_the_apps_do(string account, string identity, string hash) =>
        Assert.Equal(hash, IgnoredRepository.HashOf(Guid.Parse(account), identity));

    private static JsonElement Load()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "tests", "vectors", "ignored-repositories.json")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory.FullName, "tests", "vectors", "ignored-repositories.json")));
        return document.RootElement.Clone();
    }
}
