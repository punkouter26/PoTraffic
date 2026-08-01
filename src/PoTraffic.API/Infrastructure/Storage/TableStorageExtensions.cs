using Azure.Core;
using Azure.Data.Tables;
using Azure.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace PoTraffic.API.Infrastructure.Storage;

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
            //
            // Use ManagedIdentityCredential explicitly. DefaultAzureCredential walks env
            // vars → workload identity → chained MIs in an order that is hard to debug;
            // a deterministic credential means "the missing role is on this identity,
            // not another." If AZURE_CLIENT_ID is set (user-assigned MI), honor it;
            // otherwise fall back to the system-assigned MI created on the App Service.
            string accountName = configuration["AzureTable:AccountName"]
                ?? throw new InvalidOperationException(
                    "AzureTable:AccountName must be configured when using managed identity.");

            TokenCredential credential = ResolveManagedIdentityCredential(configuration);

            services.AddSingleton<TableServiceClient>(_ => new TableServiceClient(
                new Uri($"https://{accountName}.table.core.windows.net"),
                credential,
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

    /// <summary>
    /// Returns a <see cref="ManagedIdentityCredential"/> targeting either the
    /// user-assigned MI named by <c>AZURE_CLIENT_ID</c> or, when that env var
    /// is unset, the system-assigned MI. Centralized so any future caller
    /// (cache, scheduler, custom HttpClient) uses the same path.
    /// </summary>
    internal static TokenCredential ResolveManagedIdentityCredential(IConfiguration configuration)
    {
        string? clientId = configuration["AZURE_CLIENT_ID"];
        return string.IsNullOrWhiteSpace(clientId)
            ? new ManagedIdentityCredential(ManagedIdentityId.SystemAssigned)
            : new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(clientId));
    }
}
