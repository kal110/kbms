using KnowledgeBase.Core.Infrastructure.Persistence;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace KnowledgeBase.Api.Infrastructure;

public sealed class DatabaseHealthCheck(KbDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default) =>
        await db.Database.CanConnectAsync(ct)
            ? HealthCheckResult.Healthy("PostgreSQL reachable")
            : HealthCheckResult.Unhealthy("PostgreSQL unreachable");
}
