using System.Net;
using Microsoft.AspNetCore.Hosting;

namespace Spindle.Tests.Api;

public sealed class DownloadTests
{
    [Fact]
    public async Task Sparkle_delta_archives_are_served_without_exposing_unknown_file_types()
    {
        var ct = TestContext.Current.CancellationToken;
        var folder = Directory.CreateTempSubdirectory("spindle-downloads-");
        try
        {
            var mac = Directory.CreateDirectory(Path.Combine(folder.FullName, "mac"));
            await File.WriteAllTextAsync(Path.Combine(mac.FullName, "Spindle47-42.delta"), "delta archive", ct);
            await File.WriteAllTextAsync(Path.Combine(mac.FullName, "private.secret"), "not public", ct);
            using var factory = new ApiFactory();
            using var configured = factory.WithWebHostBuilder(builder => builder.UseSetting("App:DownloadsPath", folder.FullName));
            using var client = configured.CreateClient();
            var response = await client.GetAsync("/downloads/mac/Spindle47-42.delta", ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("application/octet-stream", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal("delta archive", await response.Content.ReadAsStringAsync(ct));
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/downloads/mac/private.secret", ct)).StatusCode);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }
}
