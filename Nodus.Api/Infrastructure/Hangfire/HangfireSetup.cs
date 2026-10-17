using Hangfire;
using Hangfire.PostgreSql;

namespace Nodus.Api.Infrastructure.Hangfire;

public static class HangfireQueues
{
    public const string AiDrafts = "ai-drafts";
    public const string AiContinuations = "ai-continuations";
    public const string Notifications = "notifications";
    public const string Maintenance = "maintenance";

    public static readonly string[] All = [AiDrafts, AiContinuations, Notifications, Maintenance, "default"];
}

public sealed class HangfireOptions
{
    public const string Section = "Hangfire";

    /// <summary>Off in tests; jobs are then neither scheduled nor executed.</summary>
    public bool Enabled { get; init; } = true;

    public int WorkerCount { get; init; } = 4;
}

public static class HangfireSetup
{
    public static IServiceCollection AddNodusHangfire(this IServiceCollection services, IConfiguration configuration,
        string connectionString)
    {
        services.AddScoped<MaintenanceJob>();
        services.AddOptions<RetentionOptions>().BindConfiguration(RetentionOptions.Section);
        var options = configuration.GetSection(HangfireOptions.Section).Get<HangfireOptions>() ?? new HangfireOptions();
        if (!options.Enabled) return services;

        services.AddHangfire(c => c
            .UseSimpleAssemblyNameTypeSerializer()
            .UseRecommendedSerializerSettings()
            .UsePostgreSqlStorage(o => o.UseNpgsqlConnection(connectionString)));
        services.AddHangfireServer(o =>
        {
            o.Queues = HangfireQueues.All;
            o.WorkerCount = options.WorkerCount;
            o.ShutdownTimeout = TimeSpan.FromSeconds(20);
        });
        return services;
    }

    public static void ScheduleRecurringJobs(this WebApplication app)
    {
        if (app.Services.GetService<IRecurringJobManager>() is not { } jobs) return;
        jobs.AddOrUpdate<MaintenanceJob>("maintenance-retention", HangfireQueues.Maintenance,
            job => job.RunAsync(CancellationToken.None), Cron.Daily(3));
    }
}
