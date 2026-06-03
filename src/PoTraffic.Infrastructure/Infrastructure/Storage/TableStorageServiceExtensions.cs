using PoTraffic.Api.Infrastructure.Storage;

namespace PoTraffic.Api.Infrastructure.Storage;

/// <summary>
/// Post-refactor: registers <see cref="TableStorageContext"/> in place of the
/// old EF Core <c>PoTrafficDbContext</c>. Handlers that took
/// <c>PoTrafficDbContext db</c> now take <c>TableStorageContext ctx</c>.
/// </summary>
public static class TableStorageServiceExtensions
{
    public static IServiceCollection AddTableStoragePersistence(this IServiceCollection services)
    {
        // Singleton so the in-memory store is shared across the request pipeline,
        // matching the old AddDbContext() singleton-like lifecycle for the host.
        services.AddSingleton<TableStorageContext>();
        return services;
    }
}
