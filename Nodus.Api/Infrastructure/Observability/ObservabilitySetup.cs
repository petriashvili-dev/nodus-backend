using System.Diagnostics;
using System.Text.RegularExpressions;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Nodus.Api.Infrastructure.Observability;

public sealed class ObservabilityOptions
{
    public const string Section = "Observability";

    /// <summary>Exposes <c>/metrics</c> for Prometheus scraping; keep it unreachable from the internet (proxy rule).</summary>
    public bool PrometheusEnabled { get; init; }

    /// <summary>OTLP endpoint for metrics and traces (Grafana Alloy, OpenTelemetry Collector); empty disables OTLP.</summary>
    public string? OtlpEndpoint { get; init; }

    /// <summary>
    /// When set, <c>/metrics</c> answers only on this port, which the reverse proxy does not route
    /// (Kestrel must also listen on it, e.g. <c>ASPNETCORE_URLS=http://+:8080;http://+:9464</c>).
    /// </summary>
    public int? MetricsPort { get; init; }

    /// <summary>JSON console logs (structured); plain text is easier to read locally.</summary>
    public bool JsonLogs { get; init; } = true;
}

public static partial class ObservabilitySetup
{
    public const string RequestIdHeader = "X-Request-Id";

    public static void AddNodusObservability(this WebApplicationBuilder builder)
    {
        var options = builder.Configuration.GetSection(ObservabilityOptions.Section).Get<ObservabilityOptions>()
                      ?? new ObservabilityOptions();
        if (options.JsonLogs && !builder.Environment.IsDevelopment())
        {
            builder.Logging.ClearProviders();
            builder.Logging.AddJsonConsole(o =>
            {
                o.IncludeScopes = true;
                o.UseUtcTimestamp = true;
                o.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
            });
        }

        var otlp = string.IsNullOrWhiteSpace(options.OtlpEndpoint) ? null : new Uri(options.OtlpEndpoint);
        var telemetry = builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService("nodus-api"))
            .WithMetrics(m =>
            {
                m.AddMeter(NodusMetrics.MeterName).AddAspNetCoreInstrumentation().AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation();
                if (options.PrometheusEnabled) m.AddPrometheusExporter();
                if (otlp is not null) m.AddOtlpExporter(o => o.Endpoint = otlp);
            });
        if (otlp is not null)
            telemetry.WithTracing(t => t
                .AddAspNetCoreInstrumentation(o => o.Filter = http => !http.Request.Path.StartsWithSegments("/health"))
                .AddHttpClientInstrumentation()
                .AddOtlpExporter(o => o.Endpoint = otlp));
    }

    public static void UseNodusObservability(this WebApplication app)
    {
        app.Use(async (http, next) =>
        {
            // Correlation id: the client's (if sane) or a new one; echoed back and attached to every log line.
            var incoming = http.Request.Headers[RequestIdHeader].ToString();
            var requestId = SafeId().IsMatch(incoming) ? incoming : Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");
            http.Response.Headers[RequestIdHeader] = requestId;
            var logger = http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Nodus.Request");
            using (logger.BeginScope(new Dictionary<string, object>
                   {
                       ["RequestId"] = requestId,
                       ["IdempotencyKey"] = http.Request.Headers["Idempotency-Key"].ToString()
                   }))
                await next(http);
        });

        var options = app.Configuration.GetSection(ObservabilityOptions.Section).Get<ObservabilityOptions>();
        if (options?.PrometheusEnabled != true) return;
        var metrics = app.MapPrometheusScrapingEndpoint("/metrics");
        if (options.MetricsPort is { } port) metrics.RequireHost($"*:{port}");
    }

    [GeneratedRegex("^[A-Za-z0-9._-]{8,64}$")]
    private static partial Regex SafeId();
}
