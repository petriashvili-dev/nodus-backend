using Microsoft.EntityFrameworkCore;
using Nodus.Api.Common;
using Nodus.Api.Endpoints;
using Nodus.Api.Infrastructure.Persistence;
using Nodus.Api.Occurrences;
using Nodus.Api.Recurrence;

namespace Nodus.Api;

/// <summary>Composition root, shared by <c>Program</c> and integration tests.</summary>
public static class NodusApplication
{
    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        configure?.Invoke(builder);
        var services = builder.Services;
        var configuration = builder.Configuration;

        services.ConfigureHttpJsonOptions(o => NodusJson.Configure(o.SerializerOptions));
        services.AddSingleton(TimeProvider.System);

        var connectionString = configuration.GetConnectionString("Postgres")
                               ?? throw new InvalidOperationException("ConnectionStrings:Postgres is required.");
        services.AddDbContext<NodusDbContext>(o => NodusDbContextFactory.Configure(o, connectionString));

        services.AddSingleton<IRecurrenceCalculator, RecurrenceCalculator>();
        services.AddSingleton<IRecurrenceProjectionService, RecurrenceProjectionService>();

        services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("postgres", tags: ["ready"]);

        var app = builder.Build();
        app.MapHealthEndpoints();
        return app;
    }

    /// <summary>
    /// <c>migrate</c> applies EF Core migrations and exits (release-controlled migrations);
    /// <c>Database:MigrateOnStartup</c> does the same before serving, for local development.
    /// </summary>
    public static async Task<bool> TryRunCommandAsync(WebApplication app, string[] args)
    {
        var migrateOnly = args.Contains("migrate");
        if (migrateOnly || app.Configuration.GetValue<bool>("Database:MigrateOnStartup"))
        {
            await using var scope = app.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<NodusDbContext>().Database.MigrateAsync();
        }

        return migrateOnly;
    }
}
