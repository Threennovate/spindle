using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Spindle.Infrastructure.Persistence;

namespace Spindle.Api.Common;

/// <summary>EF tooling creates the context without starting the web app or applying startup migrations.</summary>
public sealed class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var configuration = new ConfigurationBuilder()
            .AddUserSecrets("spindle-api")
            .AddEnvironmentVariables()
            .AddCommandLine(args)
            .Build();
        var connectionString = configuration.GetConnectionString("Default")
            ?? "Host=localhost;Port=15432;Database=Spindle;Username=spindle";
        return new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connectionString).Options);
    }
}
