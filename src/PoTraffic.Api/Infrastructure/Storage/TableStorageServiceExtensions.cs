namespace PoTraffic.Api.Infrastructure.Storage;

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
            new TableStorageContext(sp.GetRequiredService<ITableStore>()));
        return services;
    }
}
