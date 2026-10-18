using System.Reflection;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;

namespace Nodus.Api.Endpoints;

public static class HealthEndpoints
{
    public static void MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
        app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready") });
        // Which build is running: set at image build time (GIT_SHA / BUILD_TIME build args in the Dockerfile).
        app.MapGet("/version", (IConfiguration configuration) => Results.Ok(new
        {
            version = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            commit = configuration["APP_COMMIT"] ?? "unknown",
            build_time = configuration["APP_BUILD_TIME"] ?? "unknown"
        }));
    }
}
