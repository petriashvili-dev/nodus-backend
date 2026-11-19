using System.Net;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nodus.Api.AiContinuations;
using Nodus.Api.AiDrafts;
using Nodus.Api.AppConfig;
using Nodus.Api.Authentication;
using Nodus.Api.Common;
using Nodus.Api.Common.Resolvers;
using Nodus.Api.Connections;
using Nodus.Api.DataPortability;
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
using Nodus.Api.Plans;
using Nodus.Api.Recurrence;
using Nodus.Api.Sharing;
using Nodus.Api.Sync;

namespace Nodus.Api;

/// <summary>Composition root, shared by <c>Program</c> and integration tests.</summary>
public static class ApplicationHost
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
        builder.AddObservabilityServices();
        // Time for SSE streams and Hangfire workers to finish on SIGTERM.
        services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(30));
        services.ConfigureHttpJsonOptions(o => ApiJson.Configure(o.SerializerOptions));
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

        var connectionString = configuration.GetConnectionString("Postgres");
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("ConnectionStrings:Postgres is required.");
        services.AddDbContext<DatabaseContext>(o => DatabaseContextFactory.Configure(o, connectionString));
        services.AddOptions<DatabaseStartupOptions>().BindConfiguration(DatabaseStartupOptions.Section)
            .ValidateDataAnnotations().ValidateOnStart();

        services.AddAuthenticationServices(configuration, builder.Environment);
        services.AddRateLimitingServices(configuration);

        services.AddSingleton<IRecurrenceCalculator, RecurrenceCalculator>();
        services.AddSingleton<IRecurrenceProjectionService, RecurrenceProjectionService>();
        services.AddOptions<OccurrenceOptions>().BindConfiguration(OccurrenceOptions.Section);
        services.AddScoped<NodeAccess>();
        services.AddScoped<NodeHistoryLoader>();
        services.AddScoped<NodeDtoFactory>();
        services.AddScoped<NodeService>();

        services.AddSelectionResolver<ISyncEntityProvider, SyncEntityType, SyncEntityType>(
            provider => provider.EntityType, (key, input) => key == input, typeof(ApplicationHost).Assembly);
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

        services.AddOptions<PlanOptions>().BindConfiguration(PlanOptions.Section)
            .Validate(o => o.HasOnlyKnownFeatures(), $"Plans: unknown feature; known: {string.Join(", ", Features.All)}.")
            .ValidateOnStart();
        services.AddScoped<FeatureAccess>();
        services.AddScoped<PlanService>();

        services.AddOptions<AppConfigOptions>().BindConfiguration(AppConfigOptions.Section);
        services.AddScoped<AppConfigService>();

        services.AddLlmServices();
        services.AddOptions<AiOptions>().BindConfiguration(AiOptions.Section);
        services.AddScoped<AiDraftService>();
        services.AddScoped<AiDraftRunner>();
        services.AddScoped<IOutboxHandler, AiDraftRequestedOutboxHandler>();
        services.AddOptions<ContinuationOptions>().BindConfiguration(ContinuationOptions.Section);
        services.AddScoped<ContinuationTrigger>();
        services.AddScoped<SuggestionService>();
        services.AddScoped<ContinuationRunner>();
        services.AddScoped<IOutboxHandler, ContinuationRequestedOutboxHandler>();

        services.AddOptions<ImportOptions>().BindConfiguration(ImportOptions.Section);
        services.AddSelectionResolver<IImportFormatParser, ImportFormat, ImportFormat>(
            parser => parser.Format, (key, input) => key == input, typeof(ApplicationHost).Assembly);
        services.AddScoped<ExportService>();
        services.AddScoped<ImportService>();
        services.AddScoped<ImportRunner>();
        services.AddScoped<IOutboxHandler, ImportRequestedOutboxHandler>();

        services.AddBackgroundJobServices(configuration, connectionString);

        services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("postgres", tags: ["ready"]);

        overrideServices?.Invoke(services);
        var app = builder.Build();
        app.UseForwardedHeaders();
        app.UseObservability();
        app.UseExceptionHandler(e => e.Run(WriteUnhandledErrorAsync));
        IdempotencyFilter.EnableRequestBuffering(app);
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseRateLimiter();

        app.MapHealthEndpoints();
        var v1 = app.MapGroup("/v1").RequireAuthorization();
        v1.MapAuthEndpoints();
        v1.MapAppConfigEndpoints();
        v1.MapNodeEndpoints();
        v1.MapTimezoneEndpoints();
        v1.MapAiConsentEndpoints();
        v1.MapOccurrenceEndpoints();
        v1.MapSyncEndpoints();
        v1.MapSseEndpoints();
        v1.MapDeviceEndpoints();
        v1.MapConnectionEndpoints();
        v1.MapAiDraftEndpoints();
        v1.MapSuggestionEndpoints();
        v1.MapDataPortabilityEndpoints();
        return app;
    }

    /// <summary>
    /// <c>migrate</c> applies EF Core migrations and exits (release-controlled migrations);
    /// <c>Database:MigrateOnStartup</c> does the same before serving, for local development.
    /// <c>plan …</c> changes users' plans by hand and exits (see <see cref="PlanCommand"/>);
    /// <c>config …</c> edits client settings (see <see cref="AppConfigCommand"/>).
    /// </summary>
    public static async Task<bool> TryRunCommandAsync(WebApplication app, string[] args)
    {
        if (args is ["wait-for-database"] or ["wait-for-schema"])
        {
            Environment.ExitCode = await DatabaseStartup.RunAsync(app.Services, args[0] == "wait-for-schema",
                Console.Out, CancellationToken.None);
            return true;
        }
        if (args is ["plan", .. var planArgs])
        {
            Environment.ExitCode = await PlanCommand.RunAsync(app.Services, planArgs, Console.Out, CancellationToken.None);
            return true;
        }

        if (args is ["config", .. var configArgs])
        {
            Environment.ExitCode = await AppConfigCommand.RunAsync(app.Services, configArgs, Console.Out, CancellationToken.None);
            return true;
        }

        var migrateOnly = args.Contains("migrate");
        if (migrateOnly || args is ["serve"])
        {
            var code = await DatabaseStartup.RunAsync(app.Services, !migrateOnly, Console.Out, CancellationToken.None);
            if (code != 0)
            {
                Environment.ExitCode = code;
                return true;
            }
        }
        if (migrateOnly || app.Configuration.GetValue<bool>("Database:MigrateOnStartup"))
        {
            await using var scope = app.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<DatabaseContext>().Database.MigrateAsync();
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
