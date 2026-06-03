using Hangfire;
using Hangfire.SqlServer;
using PoTraffic.Api.Features.Admin;
using PoTraffic.Api.Features.Maintenance;
using PoTraffic.Api.Features.Routes;

namespace PoTraffic.Api.Infrastructure.Hangfire;

internal static class HangfireExtensions
{
    /// <summary>
    /// Registers Hangfire with SQL Server storage, the background server (worker
    /// thread), and the job classes. The background server is only registered
    /// when <c>Hangfire:DisableServer</c> is false (the default) — set that flag
    /// when SQL is unreachable so the app can boot in dev-table-storage-only
    /// mode without the worker thread crashing on its first SQL connection.
    /// </summary>
    internal static IServiceCollection AddHangfireServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        string connectionString = configuration.GetConnectionString("Default")
            ?? throw new InvalidOperationException("Default connection string is missing.");

        services.AddHangfire(cfg => cfg
            .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
            .UseSimpleAssemblyNameTypeSerializer()
            .UseRecommendedSerializerSettings()
            .UseSqlServerStorage(
                connectionString,
                new SqlServerStorageOptions
                {
                    CommandBatchMaxTimeout = TimeSpan.FromMinutes(5),
                    SlidingInvisibilityTimeout = TimeSpan.FromMinutes(5),
                    QueuePollInterval = TimeSpan.Zero,
                    UseRecommendedIsolationLevel = true,
                    DisableGlobalLocks = true
                }));

        bool disableServer = configuration.GetValue("Hangfire:DisableServer", false);
        if (!disableServer)
        {
            services.AddHangfireServer((sp, options) =>
                options.Activator = new HangfireJobActivator(sp.GetRequiredService<IServiceScopeFactory>()));
        }

        // Register Hangfire job classes in DI so HangfireJobActivator can resolve them
        services.AddScoped<PollRouteJob>();
        services.AddScoped<TripleTestShotJob>();
        services.AddScoped<PruneOldPollRecordsJob>();

        return services;
    }
}
