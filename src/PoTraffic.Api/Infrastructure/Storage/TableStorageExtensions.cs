using Azure;
using Azure.Data.Tables;
using Azure.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace PoTraffic.Api.Infrastructure.Storage;

/// <summary>
/// Azure Table Storage configuration for PoTraffic.
/// Supports both local Azurite emulation and cloud deployment.
/// </summary>
public static class TableStorageExtensions
{
    /// <summary>
    /// Connection string for local Azurite development.
    /// Default Azurite endpoints: http://127.0.0.1:10001 for Table, http://127.0.0.1:10000 for Blob/Queue
    /// </summary>
    public const string AzuriteConnectionString = 
        "DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;TableEndpoint=http://127.0.0.1:10001/";

    /// <summary>
    /// Name of the storage account in Azure.
    /// </summary>
    public const string StorageAccountName = "potraffic";

    /// <summary>
    /// Name of the resource group for the storage account.
    /// </summary>
    public const string StorageResourceGroup = "rg-potraffic";

    public static IServiceCollection AddTableStorageServices(
        this IServiceCollection services,
        IConfiguration configuration,
        IWebHostEnvironment environment)
    {
        string? connectionString = configuration["ConnectionStrings:TableStorage"];
        bool useAzurite = string.IsNullOrEmpty(connectionString) || 
                          environment.IsDevelopment() && !configuration.GetValue<bool>("AzureCredential:UseProductionStorage");

        TableClientOptions tableOptions = new()
        {
            Retry = { Mode = global::Azure.Core.RetryMode.Exponential, MaxRetries = 3, Delay = TimeSpan.FromSeconds(1) }
        };

        if (useAzurite)
        {
            // Local development: use Azurite
            services.AddSingleton<TableClient>(sp =>
            {
                TableServiceClient client = new(AzuriteConnectionString, tableOptions);
                return client.GetTableClient("TrafficPolls");
            });
            
            services.AddSingleton<TableServiceClient>(sp => 
                new TableServiceClient(AzuriteConnectionString, tableOptions));
        }
        else
        {
            // Production: use Managed Identity with Azure Table Storage
            services.AddSingleton<TableClient>(sp =>
            {
                string tableName = configuration["AzureTable:TableName"] ?? "TrafficPolls";
                TableServiceClient client = new(
                    new Uri($"https://{StorageAccountName}.table.core.windows.net"),
                    new DefaultAzureCredential(),
                    tableOptions);
                return client.GetTableClient(tableName);
            });
            
            services.AddSingleton<TableServiceClient>(sp =>
                new TableServiceClient(
                    new Uri($"https://{StorageAccountName}.table.core.windows.net"),
                    new DefaultAzureCredential(),
                    tableOptions));
        }

        return services;
    }

    /// <summary>
    /// Ensures the required tables exist in Azure Table Storage or Azurite.
    /// Call this during application startup.
    /// </summary>
    public static async Task EnsureTablesExistAsync(TableServiceClient tableServiceClient, string tableName)
    {
        await tableServiceClient.CreateTableIfNotExistsAsync(tableName);
    }
}

/// <summary>
/// Entity for storing traffic poll results in Azure Table Storage.
/// </summary>
public class TrafficPollEntity : global::Azure.Data.Tables.ITableEntity
{
    public string PartitionKey { get; set; } = string.Empty;
    public string RowKey { get; set; } = string.Empty;
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }
    
    // Route information
    public Guid RouteId { get; set; }
    public Guid UserId { get; set; }
    
    // Poll results
    public int DurationSeconds { get; set; }
    public int DistanceMetres { get; set; }
    public DateTimeOffset PollTimeUtc { get; set; }
    
    // Provider information
    public string Provider { get; set; } = string.Empty;
    
    // Traffic conditions
    public string TrafficLevel { get; set; } = string.Empty;
    public double ConfidenceScore { get; set; }
}
