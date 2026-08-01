using Microsoft.Extensions.Diagnostics.HealthChecks;
using PoTraffic.API.Infrastructure.Scheduling;
using PoTraffic.Shared.DTOs.Admin;

namespace PoTraffic.API.Infrastructure;

/// <summary>
/// Builds the per-dependency connection status list rendered by the <c>/health</c> Blazor
/// page and returned by <c>/api/admin/connection-health</c>. Both surfaces share this one
/// implementation so they can never drift.
///
/// <para>
/// Statuses only — never a secret value. "Configured / Not configured" is the most that is
/// ever said about a key, which matches what the anonymous health-check endpoint already
/// discloses.
/// </para>
/// </summary>
internal static class ConnectionHealthReporter
{
    internal static async Task<IReadOnlyList<ConnectionHealthDto>> BuildAsync(
        HealthCheckService healthCheckService,
        IConfiguration configuration,
        CancellationToken ct)
    {
        HealthReport report = await healthCheckService.CheckHealthAsync(ct);

        List<ConnectionHealthDto> results = report.Entries.Select(e => new ConnectionHealthDto(
            Name: e.Key,
            Status: e.Value.Status.ToString(),
            Description: e.Value.Description,
            DurationMs: e.Value.Duration.TotalMilliseconds)).ToList();

        // Background scheduler — surface last-tick health (not a ping, but the most
        // common local-dev breakage). Status maps to the same "Healthy"/other badge.
        results.Add(DescribeScheduler(BackgroundSchedulerService.LastTick));

        // Google Maps API key — report configured/not without leaking the value.
        bool mapsKeyConfigured = !string.IsNullOrWhiteSpace(configuration["GoogleMaps:ApiKey"]);
        results.Add(new ConnectionHealthDto(
            Name: "Google Maps API Key",
            Status: mapsKeyConfigured ? "Healthy" : "Unhealthy",
            Description: mapsKeyConfigured ? "Configured" : "Not configured",
            DurationMs: 0));

        return results;
    }

    private static ConnectionHealthDto DescribeScheduler(SchedulerTickStatus tick)
    {
        (string status, string description) = tick switch
        {
            { LastTickUtc: null } =>
                ("Unhealthy", "Scheduler has not completed a tick yet."),
            { Succeeded: true } =>
                ("Healthy", $"Last tick {tick.LastTickUtc:O} succeeded."),
            _ =>
                ("Unhealthy", $"Last tick {tick.LastTickUtc:O} failed: {tick.Error}"),
        };

        return new ConnectionHealthDto("Background Scheduler", status, description, DurationMs: 0);
    }
}
