using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Nodus.Api.Infrastructure.Persistence;

/// <summary>EF tooling can build the model without a database; connecting requires explicit configuration.</summary>
public sealed class DatabaseContextFactory : IDesignTimeDbContextFactory<DatabaseContext>
{
    public DatabaseContext CreateDbContext(string[] args)
    {
        var configuration = new ConfigurationBuilder()
            .AddEnvironmentVariables()
            .AddCommandLine(args)
            .Build();
        var builder = new DbContextOptionsBuilder<DatabaseContext>();
        Configure(builder, configuration.GetConnectionString("Postgres"));
        return new DatabaseContext(builder.Options);
    }

    public static void Configure(DbContextOptionsBuilder builder, string? connectionString) =>
        builder.UseNpgsql(connectionString).UseSnakeCaseNamingConvention();
}
