using Microsoft.Extensions.Diagnostics.HealthChecks;
using PoTraffic.Api.Infrastructure.Scheduling;
using PoTraffic.Api.Infrastructure.Storage;

namespace PoTraffic.Api.Infrastructure;

/// <summary>
/// Degraded when the context fell back to memory-only mode; Unhealthy when the durable
/// store is configured but not actually reachable right now. The old check only read the
/// startup <c>IsDurable</c> flag, so a post-startup Table Storage outage (every write
/// failing) still reported Healthy — masking the outage from App Service probes.
/// </summary>
public sealed class StorageHealthCheck(TableStorageContext db) : IHealthCheck
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        if (!db.IsDurable)
            return HealthCheckResult.Degraded("MEMORY-ONLY mode — start Azurite (docker compose up -d) to persist data");

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(ProbeTimeout);
            await db.ProbeStoreAsync(cts.Token);
            return HealthCheckResult.Healthy("Table Storage reachable");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy($"Table Storage probe failed: {ex.Message.Split('\n')[0]}");
        }
    }
}

/// <summary>
/// Surfaces background-scheduler liveness: a tick is expected every second, so a
/// stale or failed tick means polling and pruning have silently stopped.
/// </summary>
public sealed class SchedulerHealthCheck(IServiceProvider services) : IHealthCheck
{
    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(30);

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        // Not registered at all (Testing host) or explicitly no-op → nothing to monitor.
        if (services.GetService<IJobScheduler>() is null or NoOpJobScheduler)
            return Task.FromResult(HealthCheckResult.Healthy("Scheduler disabled (Testing)"));

        SchedulerTickStatus tick = BackgroundSchedulerService.LastTick;
        if (tick.LastTickUtc is null)
            return Task.FromResult(HealthCheckResult.Degraded("Scheduler has not completed a tick yet"));
        if (DateTimeOffset.UtcNow - tick.LastTickUtc > StaleAfter)
            return Task.FromResult(HealthCheckResult.Unhealthy(
                $"Scheduler stalled — last tick {tick.LastTickUtc:O}"));
        if (!tick.Succeeded)
            return Task.FromResult(HealthCheckResult.Degraded($"Last scheduler tick failed: {tick.Error}"));
        return Task.FromResult(HealthCheckResult.Healthy($"Last tick {tick.LastTickUtc:O}"));
    }
}
