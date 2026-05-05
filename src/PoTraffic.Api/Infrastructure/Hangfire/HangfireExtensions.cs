using Hangfire;
using Hangfire.SqlServer;
using PoTraffic.Api.Features.Admin;
using PoTraffic.Api.Features.Maintenance;
using PoTraffic.Api.Features.Routes;

namespace PoTraffic.Api.Infrastructure.Hangfire;

internal static class HangfireExtensions
{
    /// <summary>
    /// Registers Hangfire with SQL Server storage and wires job classes into DI.
    /// Adapter pattern — HangfireJobActivator bridges Hangfire job activation to ASP.NET Core DI scope lifecycle.
    /// </summary>
    internal static IServiceCollection AddHangfireServices(this IServiceCollection services, IConfiguration configuration)
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

        services.AddHangfireServer((sp, options) =>
            options.Activator = new HangfireJobActivator(sp.GetRequiredService<IServiceScopeFactory>()));

        // Register Hangfire job classes in DI so HangfireJobActivator can resolve them
        services.AddScoped<PollRouteJob>();
        services.AddScoped<TripleTestShotJob>();
        services.AddScoped<PruneOldPollRecordsJob>();

        return services;
    }
}
