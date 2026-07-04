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

        // Rule 5 — Dynamic Switching, corrected for cloud (Rule 6 — Managed Identity, no
        // connection strings in prod):
        //   • Explicit "UseDevelopmentStorage=true"        → local Azurite.
        //   • Local Development with no connection string  → local Azurite.
        //   • Explicit non-dev connection string           → use it (Testcontainers / custom endpoint).
        //   • Any DEPLOYED environment with no connection string → Managed Identity against
        //     AzureTable:AccountName.
        // An EMPTY connection string must NEVER silently fall back to the 127.0.0.1 emulator
        // outside Development — that is exactly what crashed prod (500.30, socket 127.0.0.1:10002).
        bool explicitDevStorage = !string.IsNullOrEmpty(connectionString)
            && connectionString.Contains("UseDevelopmentStorage=true", StringComparison.OrdinalIgnoreCase);

        bool useAzurite = explicitDevStorage
            || (environment.IsDevelopment()
                && string.IsNullOrEmpty(connectionString)
                && !configuration.GetValue<bool>("AzureCredential:UseProductionStorage"));

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
