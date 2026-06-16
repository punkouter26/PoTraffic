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
    /// worker service.
    /// Skipped in the Testing environment where Azurite is not running.
    /// </summary>
    public static IServiceCollection AddBackgroundJobScheduler(
        this IServiceCollection services,
        IWebHostEnvironment environment)
    {
        if (environment.IsEnvironment("Testing"))
            return services;

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
