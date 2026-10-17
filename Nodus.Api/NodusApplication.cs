using System.Net;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nodus.Api.AiContinuations;
using Nodus.Api.AiDrafts;
using Nodus.Api.Authentication;
using Nodus.Api.Common;
using Nodus.Api.Connections;
using Nodus.Api.Devices;
using Nodus.Api.Endpoints;
using Nodus.Api.Infrastructure.Apns;
using Nodus.Api.Infrastructure.Hangfire;
using Nodus.Api.Infrastructure.Llm;
using Nodus.Api.Infrastructure.Observability;
using Nodus.Api.Infrastructure.Outbox;
using Nodus.Api.Infrastructure.Persistence;
using Nodus.Api.Infrastructure.Sse;
using Nodus.Api.Nodes;
using Nodus.Api.Notifications;
using Nodus.Api.Occurrences;
using Nodus.Api.Recurrence;
using Nodus.Api.Sharing;
using Nodus.Api.Sync;

namespace Nodus.Api;

/// <summary>Composition root, shared by <c>Program</c> and integration tests.</summary>
public static class NodusApplication
{
    /// <param name="configure">
    /// Runs before the application registrations; tests use it to add configuration and to pre-register
    /// replacements, which win because infrastructure defaults are registered with <c>TryAdd</c>.
    /// </param>
    /// <param name="overrideServices">Runs after all registrations; tests replace external clients here.</param>
    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null,
        Action<IServiceCollection>? overrideServices = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        configure?.Invoke(builder);
        var services = builder.Services;
        var configuration = builder.Configuration;

        builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = Limits.RequestBodyBytes);
        builder.AddNodusObservability();
        // Time for SSE streams and Hangfire workers to finish on SIGTERM.
        services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(30));
        services.ConfigureHttpJsonOptions(o => NodusJson.Configure(o.SerializerOptions));
        services.AddProblemDetails();
        services.TryAddSingleton(TimeProvider.System);
        services.Configure<ForwardedHeadersOptions>(o =>
        {
            // TLS terminates on the reverse proxy in front of the single API container.
            o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            if (configuration.GetValue<bool>("ReverseProxy:TrustForwardedHeaders"))
            {
                o.KnownIPNetworks.Clear();
                o.KnownProxies.Clear();
            }
        });

        var connectionString = configuration.GetConnectionString("Postgres")
                               ?? throw new InvalidOperationException("ConnectionStrings:Postgres is required.");
        services.AddDbContext<NodusDbContext>(o => NodusDbContextFactory.Configure(o, connectionString));

        services.AddNodusAuthentication(configuration);
        services.AddNodusRateLimiting(configuration);

        services.AddSingleton<IRecurrenceCalculator, RecurrenceCalculator>();
        services.AddSingleton<IRecurrenceProjectionService, RecurrenceProjectionService>();
        services.AddOptions<OccurrenceOptions>().BindConfiguration(OccurrenceOptions.Section);
        services.AddScoped<NodeAccess>();
        services.AddScoped<NodeHistoryLoader>();
        services.AddScoped<NodeDtoFactory>();
        services.AddScoped<NodeService>();

        services.AddScoped<ChangeJournal>();
        services.AddScoped<UnitOfWork>();
        services.AddOptions<OutboxOptions>().BindConfiguration(OutboxOptions.Section);
        services.AddSingleton<OutboxSignal>();
        services.AddSingleton<OutboxDispatcher>();
        services.AddHostedService(sp => sp.GetRequiredService<OutboxDispatcher>());
        services.AddSingleton<IRealtimeHub, InMemoryRealtimeHub>();
        services.AddScoped<IOutboxHandler, SyncChangedOutboxHandler>();

        services.AddOptions<SseOptions>().BindConfiguration(SseOptions.Section);
        services.AddOptions<ApnsOptions>().BindConfiguration(ApnsOptions.Section);
        services.AddSingleton<ApnsTokenProvider>();
        services.AddHttpClient<IApnsClient, ApnsClient>(c =>
            {
                c.DefaultRequestVersion = HttpVersion.Version20;
                c.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact;
                c.Timeout = TimeSpan.FromSeconds(15);
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(30),
                EnableMultipleHttp2Connections = true
            });
        services.AddOptions<ConnectionOptions>().BindConfiguration(ConnectionOptions.Section);
        services.AddScoped<ConnectionService>();
        services.AddScoped<SharingService>();
        services.AddScoped<PushNotifier>();
        services.AddScoped<PushDeliveryJob>();
        services.AddScoped<IOutboxHandler, PushRequestedOutboxHandler>();

        services.AddNodusLlm();
        services.AddOptions<AiOptions>().BindConfiguration(AiOptions.Section);
        services.AddScoped<AiDraftService>();
        services.AddScoped<AiDraftRunner>();
        services.AddScoped<IOutboxHandler, AiDraftRequestedOutboxHandler>();
        services.AddOptions<ContinuationOptions>().BindConfiguration(ContinuationOptions.Section);
        services.AddScoped<ContinuationTrigger>();
        services.AddScoped<SuggestionService>();
        services.AddScoped<ContinuationRunner>();
        services.AddScoped<IOutboxHandler, ContinuationRequestedOutboxHandler>();

        services.AddNodusHangfire(configuration, connectionString);

        services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("postgres", tags: ["ready"]);

        overrideServices?.Invoke(services);
        var app = builder.Build();
        app.UseForwardedHeaders();
        app.UseNodusObservability();
        app.UseExceptionHandler(e => e.Run(WriteUnhandledErrorAsync));
        IdempotencyFilter.EnableRequestBuffering(app);
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseRateLimiter();

        app.MapHealthEndpoints();
        var v1 = app.MapGroup("/v1").RequireAuthorization();
        v1.MapAuthEndpoints();
        v1.MapNodeEndpoints();
        v1.MapTimezoneEndpoints();
        v1.MapOccurrenceEndpoints();
        v1.MapSyncEndpoints();
        v1.MapSseEndpoints();
        v1.MapDeviceEndpoints();
        v1.MapConnectionEndpoints();
        v1.MapAiDraftEndpoints();
        v1.MapSuggestionEndpoints();
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

        // Touches Hangfire storage, so it runs only when serving, never for the migrate command.
        if (!migrateOnly) app.ScheduleRecurringJobs();
        return migrateOnly;
    }

    /// <summary>Malformed bodies become 400; anything else is a 500 without internal details.</summary>
    private static Task WriteUnhandledErrorAsync(HttpContext http)
    {
        var error = http.Features.Get<IExceptionHandlerFeature>()?.Error;
        var result = error is BadHttpRequestException bad
            ? ApiResult.Problem(bad.StatusCode, "bad_request", "The request is malformed.")
            : ApiResult.Problem(StatusCodes.Status500InternalServerError, "internal_error", "An unexpected error occurred.");
        return result.ExecuteAsync(http);
    }
}
