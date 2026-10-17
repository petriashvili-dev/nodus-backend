using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Nodus.Api.Infrastructure.Persistence;

public sealed class DatabaseHealthCheck(NodusDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default) =>
        await db.Database.CanConnectAsync(ct)
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy("PostgreSQL is unreachable.");
}
