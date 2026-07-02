using Azure.Data.Tables;
using Microsoft.Extensions.DependencyInjection;

namespace PoTraffic.Api.Infrastructure.Scheduling;

/// <summary>
/// DI registration for the custom background job scheduler.
/// Uses a lightweight Azure Table Storage-backed BackgroundService.
/// </summary>
public static class BackgroundSchedulerServiceExtensions
{
    /// <summary>
    /// Registers the job scheduler, Table Storage persistence, and the background
    /// worker service. Testing gets a no-op scheduler (no worker) so handlers
    /// depending on <see cref="IJobScheduler"/> resolve in a standalone Testing host.
    /// </summary>
    public static IServiceCollection AddBackgroundJobScheduler(
        this IServiceCollection services,
        IWebHostEnvironment environment)
    {
        if (environment.IsEnvironment("Testing"))
        {
            services.AddSingleton<IJobScheduler>(new NoOpJobScheduler());
            return services;
        }

        // Register scheduler with a factory that catches Azurite connection errors
        // at resolution time, falling back to no-op if Table Storage is unavailable.
        services.AddSingleton<IJobScheduler>(sp =>
        {
            try
            {
                return new TableStorageJobScheduler(
                    sp.GetRequiredService<TableServiceClient>());
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[startup] BackgroundJobScheduler disabled — {ex.Message}");
                return new NoOpJobScheduler();
            }
        });

        services.AddHostedService<BackgroundSchedulerService>();
        return services;
    }
}
