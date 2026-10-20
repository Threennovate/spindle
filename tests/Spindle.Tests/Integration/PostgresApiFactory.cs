using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Npgsql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Spindle.Api.Subscriptions;
using Spindle.Infrastructure.Email;
using Spindle.Infrastructure.Persistence;

namespace Spindle.Tests.Integration;

/// <summary>
/// Runs the API against a throwaway database on a real PostgreSQL. The database is created by applying
/// the migrations, so they are tested too, and dropped when the test run ends.
/// </summary>
public sealed class PostgresApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private const string ConnectionStringName = "PostgresTests";

    private readonly string connectionString = CreateConnectionString();

    public CapturingEmailSender Emails { get; } = new();

    /// <summary>Google and Apple, answering the server's requests to them.</summary>
    public FakeIdentityProviders IdentityProviders { get; } = new();

    public AsyncServiceScope CreateScope() => Services.CreateAsyncScope();

    public async ValueTask InitializeAsync()
    {
        await using var scope = CreateScope();
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();
    }

    public override async ValueTask DisposeAsync()
    {
        await using (var scope = CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureDeletedAsync();
        }

        await base.DisposeAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Default", connectionString);
        builder.UseSetting("Auth:SigningKey", new string('k', 32));
        builder.UseSetting("App:BaseUrl", "https://track.example.com");
        builder.UseSetting("Smtp:Host", "localhost");
        builder.UseSetting("Smtp:FromAddress", "no-reply@example.com");
        builder.UseSetting("RateLimits:AccountEmailsPerQuarterHour", "1000");
        builder.UseSetting("RateLimits:OAuthRegistrationsPerHour", "1000");
        // As on the hosted service. Tests of a self-hosted server turn it off for themselves.
        builder.UseSetting("Subscriptions:Enabled", "true");
        builder.UseSetting("RateLimits:ExternalSignInRequestsPerMinute", "1000");
        builder.UseSetting("Auth:Google:ClientId", FakeIdentityProviders.GoogleClientId);
        builder.UseSetting("Auth:Google:ClientSecret", "google-secret");
        builder.UseSetting("Auth:Apple:ServicesId", FakeIdentityProviders.AppleServicesId);
        builder.UseSetting("Auth:Apple:TeamId", "TEAM123456");
        builder.UseSetting("Auth:Apple:KeyId", "KEY1234567");
        // As a one-line setting would have it.
        builder.UseSetting("Auth:Apple:PrivateKey", IdentityProviders.ApplePrivateKeyPem.Replace("\n", "\\n", StringComparison.Ordinal));
        builder.ConfigureTestServices(services =>
        {
            services.AddHttpClient(Spindle.Api.Auth.OidcSignInProvider.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => IdentityProviders);
            services.RemoveAll<IEmailSender>();
            services.AddSingleton<IEmailSender>(Emails);
            // Tests send the reminders themselves, when the trials are where they want them.
            services.Remove(services.Single(s => s.ImplementationType == typeof(TrialReminderService)));
        });
    }

    private static string CreateConnectionString()
    {
        var configuration = new ConfigurationBuilder()
            .AddUserSecrets("spindle-tests")
            .AddEnvironmentVariables()
            .Build();

        var server = configuration.GetConnectionString(ConnectionStringName);
        if (string.IsNullOrWhiteSpace(server))
        {
            throw new InvalidOperationException(
                "Integration tests need PostgreSQL. Run scripts/local-db.sh, "
                + $"or set the environment variable ConnectionStrings__{ConnectionStringName}.");
        }

        return new NpgsqlConnectionStringBuilder(server)
        {
            Database = $"Spindle_Tests_{Guid.NewGuid():N}",
        }.ConnectionString;
    }
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresApiFactory>
{
    public const string Name = "PostgreSQL";
}
