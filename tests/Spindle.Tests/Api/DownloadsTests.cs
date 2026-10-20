using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Spindle.Tests.Api;

/// <summary>The downloads folder: the Mac and Windows apps, their update feeds, and the iPhone app.</summary>
public sealed class DownloadsTests(ApiFactory factory) : IClassFixture<ApiFactory>, IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("spindle-downloads-").FullName;
    private readonly string webRoot = Directory.CreateTempSubdirectory("spindle-web-").FullName;

    public void Dispose()
    {
        Directory.Delete(folder, recursive: true);
        Directory.Delete(webRoot, recursive: true);
    }

    // With a web app, as when published, so a path the downloads don't have could fall through to its index.html.
    private HttpClient Client(string? downloadsPath = null)
    {
        File.WriteAllText(Path.Combine(webRoot, "index.html"), "<!doctype html><title>Spindle</title>");
        return factory.WithWebHostBuilder(builder => builder
            .UseSetting("App:DownloadsPath", downloadsPath ?? folder)
            .UseWebRoot(webRoot))
            .CreateClient();
    }

    [Fact]
    public async Task Serves_the_disk_image_and_the_feed_with_their_types()
    {
        Directory.CreateDirectory(Path.Combine(folder, "mac"));
        await File.WriteAllTextAsync(Path.Combine(folder, "mac", "appcast.xml"), "<rss/>", TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(folder, "mac", "Spindle-1.0.0.dmg"), [1, 2, 3], TestContext.Current.CancellationToken);
        var client = Client();

        var feed = await client.GetAsync("/downloads/mac/appcast.xml", TestContext.Current.CancellationToken);
        var image = await client.GetAsync("/downloads/mac/Spindle-1.0.0.dmg", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, feed.StatusCode);
        Assert.Equal("text/xml", feed.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no-cache", feed.Headers.CacheControl?.ToString());
        Assert.Equal(HttpStatusCode.OK, image.StatusCode);
        Assert.Equal("application/x-apple-diskimage", image.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no-cache", image.Headers.CacheControl?.ToString());
        Assert.Equal([1, 2, 3], await image.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Serves_the_windows_installer_feed_and_packages_with_their_types()
    {
        Directory.CreateDirectory(Path.Combine(folder, "spindle-windows"));
        await File.WriteAllBytesAsync(Path.Combine(folder, "Spindle-Setup.exe"), [77, 90], TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(folder, "spindle-windows", "releases.win-x64.json"), "{\"Assets\":[]}", TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(folder, "spindle-windows", "Spindle.Desktop-1.0.0-win-x64-full.nupkg"), [80, 75], TestContext.Current.CancellationToken);
        var client = Client();

        var installer = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/downloads/Spindle-Setup.exe"), TestContext.Current.CancellationToken);
        var feed = await client.GetAsync("/downloads/spindle-windows/releases.win-x64.json", TestContext.Current.CancellationToken);
        var package = await client.GetAsync("/downloads/spindle-windows/Spindle.Desktop-1.0.0-win-x64-full.nupkg", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, installer.StatusCode);
        Assert.Equal("application/vnd.microsoft.portable-executable", installer.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.OK, feed.StatusCode);
        Assert.Equal("application/json", feed.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no-cache", feed.Headers.CacheControl?.ToString());
        Assert.Equal(HttpStatusCode.OK, package.StatusCode);
        Assert.Equal("application/octet-stream", package.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Serves_the_iphone_app_its_manifest_and_install_page()
    {
        Directory.CreateDirectory(Path.Combine(folder, "ios"));
        await File.WriteAllBytesAsync(Path.Combine(folder, "ios", "Spindle.ipa"), [80, 75], TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(folder, "ios", "manifest.plist"), "<plist/>", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(folder, "ios", "install.html"), "<!doctype html>", TestContext.Current.CancellationToken);
        var client = Client();

        var package = await client.GetAsync("/downloads/ios/Spindle.ipa", TestContext.Current.CancellationToken);
        var manifest = await client.GetAsync("/downloads/ios/manifest.plist", TestContext.Current.CancellationToken);
        var page = await client.GetAsync("/downloads/ios/install.html", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, package.StatusCode);
        Assert.Equal("application/octet-stream", package.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.OK, manifest.StatusCode);
        Assert.Equal("application/xml", manifest.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal("text/html", page.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task A_relative_folder_is_found_from_the_site()
    {
        Directory.CreateDirectory(Path.Combine(folder, "ios"));
        await File.WriteAllTextAsync(Path.Combine(folder, "ios", "install.html"), "<!doctype html>", TestContext.Current.CancellationToken);
        var site = factory.Services.GetRequiredService<IWebHostEnvironment>().ContentRootPath;
        var client = Client(Path.GetRelativePath(site, folder));

        var page = await client.GetAsync("/downloads/ios/install.html", TestContext.Current.CancellationToken);

        Assert.StartsWith("..", Path.GetRelativePath(site, folder), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
    }

    [Theory]
    [InlineData("/downloads/mac/appcast.xml")]
    [InlineData("/downloads/mac")]
    [InlineData("/downloads")]
    public async Task A_missing_download_is_a_404_not_the_web_app(string path)
    {
        var client = Client();

        var response = await client.GetAsync(path, TestContext.Current.CancellationToken);
        var appRoute = await client.GetAsync("/reports", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, appRoute.StatusCode);
    }

    [Fact]
    public async Task The_short_link_leads_to_the_latest_mac_app()
    {
        var client = factory.WithWebHostBuilder(_ => { }).CreateClient(new() { AllowAutoRedirect = false });

        var response = await client.GetAsync("/download/mac", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/downloads/Spindle.dmg", response.Headers.Location?.ToString());
    }

    [Theory]
    [InlineData("/download/windows", "/downloads/Spindle-Setup.exe")]
    [InlineData("/download/windows-arm64", "/downloads/Spindle-Setup-arm64.exe")]
    public async Task The_short_links_lead_to_the_latest_windows_installers(string link, string file)
    {
        var client = factory.WithWebHostBuilder(_ => { }).CreateClient(new() { AllowAutoRedirect = false });

        var response = await client.GetAsync(link, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(file, response.Headers.Location?.ToString());
    }
}
