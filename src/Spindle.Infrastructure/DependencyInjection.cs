using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Spindle.Infrastructure.Email;
using Spindle.Infrastructure.Persistence;

namespace Spindle.Infrastructure;

public static class DependencyInjection
{
    public const string ConnectionStringName = "Default";

    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration,
        bool validateOnStart)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<EntityLifecycleInterceptor>();

        var databaseOptions = services.AddOptions<DatabaseOptions>()
            .Configure(options => options.ConnectionString = configuration.GetConnectionString(ConnectionStringName))
            .Validate(
                options => !string.IsNullOrWhiteSpace(options.ConnectionString),
                $"Connection string '{ConnectionStringName}' is not configured. See CONTRIBUTING.md or docs/self-hosting.md.");

        if (validateOnStart)
        {
            databaseOptions.ValidateOnStart();
        }

        var smtpOptions = services.AddOptions<SmtpOptions>()
            .Bind(configuration.GetSection(SmtpOptions.SectionName))
            .ValidateDataAnnotations();
        if (validateOnStart)
        {
            smtpOptions.ValidateOnStart();
        }

        services.AddSingleton<IEmailSender, SmtpEmailSender>();

        // Short-lived tokens (email confirmation, password reset, passkey challenges) are bound to this name.
        var dataProtection = services.AddDataProtection().SetApplicationName("Spindle");
        if (validateOnStart)
        {
            // Not while the build only describes the API: there is no database then.
            dataProtection.PersistKeysToDbContext<AppDbContext>();
        }

        services.AddDbContext<AppDbContext>((serviceProvider, options) =>
        {
            var database = serviceProvider.GetRequiredService<IOptions<DatabaseOptions>>().Value;

            options.UseNpgsql(database.ConnectionString, postgres => postgres.EnableRetryOnFailure());
            options.AddInterceptors(serviceProvider.GetRequiredService<EntityLifecycleInterceptor>());
            // Interceptors the app adds, such as the one that tells other devices about changes.
            options.AddInterceptors(serviceProvider.GetServices<ISaveChangesInterceptor>());
        });

        return services;
    }
}

public sealed class DatabaseOptions
{
    public string? ConnectionString { get; set; }
}
