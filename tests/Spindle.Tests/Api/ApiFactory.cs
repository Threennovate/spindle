using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Spindle.Tests.Api;

/// <summary>
/// Runs the API in memory for tests that stop before the database: routing, validation and authentication.
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Not "Development", so a developer's user secrets (and their database) are never loaded.
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Default", "Host=database.invalid;Database=unused;Timeout=1");
        builder.UseSetting("Auth:SigningKey", new string('k', 32));
        builder.UseSetting("App:BaseUrl", "https://track.example.com");
        builder.UseSetting("Smtp:Host", "localhost");
        builder.UseSetting("Smtp:FromAddress", "no-reply@example.com");

        // The keys otherwise live in the database, and loading them at startup would retry for a minute.
        builder.ConfigureServices(services => services.AddDataProtection()
            .PersistKeysToFileSystem(Directory.CreateTempSubdirectory("spindle-keys-")));
    }
}
