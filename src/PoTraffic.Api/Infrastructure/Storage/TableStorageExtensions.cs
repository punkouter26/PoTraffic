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
    // NOTE: the Table endpoint MUST include the account path segment (/devstoreaccount1).
    // Without it the Azure SDK targets http://127.0.0.1:10002/Tables, which Azurite
    // rejects with 400 Bad Request — the cause of the every-tick scheduler failures.
    public const string AzuriteConnectionString =
        "DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;TableEndpoint=http://127.0.0.1:10002/devstoreaccount1;";

    public static IServiceCollection AddTableStorageServices(
        this IServiceCollection services,
        IConfiguration configuration,
        IWebHostEnvironment environment)
    {
        string? connectionString = configuration["ConnectionStrings:TableStorage"];

        // Rule 5 — Dynamic Switching: Azurite locally, Azure Table Storage in cloud.
        //   Empty / "UseDevelopmentStorage=true" → local Azurite default.
        //   Explicit connection string → custom Azurite/Azure endpoint, used by Testcontainers.
        //   Managed identity → Azure Table Storage account from AzureTable:AccountName config.
        bool useAzurite = string.IsNullOrEmpty(connectionString)
            || connectionString.Contains("UseDevelopmentStorage=true", StringComparison.OrdinalIgnoreCase)
            || (environment.IsDevelopment() && !configuration.GetValue<bool>("AzureCredential:UseProductionStorage"));

        TableClientOptions tableOptions = new()
        {
            Retry = { Mode = global::Azure.Core.RetryMode.Exponential, MaxRetries = 3, Delay = TimeSpan.FromSeconds(1) }
        };

        if (useAzurite)
        {
            services.AddSingleton<TableServiceClient>(_ =>
                new TableServiceClient(AzuriteConnectionString, tableOptions));
        }
        else if (!string.IsNullOrWhiteSpace(connectionString)
            && !configuration.GetValue<bool>("AzureTable:UseManagedIdentity"))
        {
            services.AddSingleton<TableServiceClient>(_ =>
                new TableServiceClient(connectionString, tableOptions));
        }
        else
        {
            // Production: managed identity against the account named in configuration
            // (AzureTable:AccountName — set in App Service application settings).
            string accountName = configuration["AzureTable:AccountName"]
                ?? throw new InvalidOperationException(
                    "AzureTable:AccountName must be configured when using managed identity.");
            services.AddSingleton<TableServiceClient>(_ =>
                new TableServiceClient(
                    new Uri($"https://{accountName}.table.core.windows.net"),
                    new DefaultAzureCredential(),
                    tableOptions));
        }

        // Scheduler job-state table client (see TableStorageJobScheduler).
        services.AddSingleton<TableClient>(sp =>
        {
            string tableName = configuration["AzureTable:TableName"] ?? "TrafficPolls";
            return sp.GetRequiredService<TableServiceClient>().GetTableClient(tableName);
        });

        return services;
    }
}
