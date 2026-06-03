using Hangfire;
using PoTraffic.Api.Features.Admin;
using PoTraffic.Api.Features.Maintenance;
using PoTraffic.Api.Features.Routes;

namespace PoTraffic.Api.Infrastructure.Hangfire;

internal static class HangfireExtensions
{
    /// <summary>
    /// Registers Hangfire with in-memory storage, the background server (worker
    /// thread), and the job classes. In-memory storage is used for all environments
    /// — dev/test uses Azurite for Table Storage, and Hangfire jobs are ephemeral
    /// background tasks that don't require durable SQL persistence.
    /// In the Testing environment the background server is skipped entirely so
    /// the WebApplicationFactory host can start without a worker thread.
    /// </summary>
    internal static IServiceCollection AddHangfireServices(
        this IServiceCollection services,
        IConfiguration configuration,
        IWebHostEnvironment environment)
    {
        services.AddHangfire(cfg => cfg
            .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
            .UseSimpleAssemblyNameTypeSerializer()
            .UseRecommendedSerializerSettings()
            .UseInMemoryStorage());

        // In Testing, skip the background server so integration tests can boot
        // without a worker thread polling for jobs.
        if (!environment.IsEnvironment("Testing"))
        {
            bool disableServer = configuration.GetValue("Hangfire:DisableServer", false);
            if (!disableServer)
            {
                services.AddHangfireServer((sp, options) =>
                    options.Activator = new HangfireJobActivator(sp.GetRequiredService<IServiceScopeFactory>()));
            }
        }

        // Register Hangfire job classes in DI so HangfireJobActivator can resolve them
        services.AddScoped<PollRouteJob>();
        services.AddScoped<TripleTestShotJob>();
        services.AddScoped<PruneOldPollRecordsJob>();

        return services;
    }
}
