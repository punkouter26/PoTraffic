using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace PoTraffic.API.Infrastructure.Storage;

/// <summary>
/// Registers <see cref="TableStorageContext"/> (singleton working set) backed by
/// <see cref="AzureTableStore"/> for durable writes. The store resolves the
/// <c>TableServiceClient</c> configured in <see cref="TableStorageExtensions"/> —
/// Azurite locally, managed identity in the cloud.
/// </summary>
public static class TableStorageServiceExtensions
{
    public static IServiceCollection AddTableStoragePersistence(this IServiceCollection services)
    {
        services.AddSingleton<ITableStore, AzureTableStore>();
        services.AddSingleton<TableStorageContext>(sp =>
        {
            TableStorageContext ctx = new(sp.GetRequiredService<ITableStore>());
            // Hand the logger factory to the context so the source-generated
            // HydrationLog can produce zero-allocation events without exposing a
            // public DI dependency on the context's constructor.
            ctx.LoggerFactory = sp.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance;
            return ctx;
        });
        return services;
    }
}
