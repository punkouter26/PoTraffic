using Microsoft.Extensions.Hosting;

namespace PoTraffic.Api.Infrastructure.Scheduling;

/// <summary>
/// No-op hosted service used as a placeholder when the real BackgroundSchedulerService
/// cannot be created (e.g. Azurite is unavailable).
/// </summary>
internal sealed class NoopBackgroundService : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.CompletedTask;
}
