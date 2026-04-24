using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Configuration;

namespace PoTraffic.Api.Infrastructure.Azure;

/// <summary>
/// Azure Managed Identity configuration for PoTraffic.
/// Uses DefaultAzureCredential which tries multiple authentication methods in order:
/// 1. Managed Identity (Azure App Service, Azure VM, Azure Container Apps)
/// 2. Visual Studio credentials
/// 3. Azure CLI credentials
/// 4. Azure PowerShell credentials
/// 5. Environment variables
/// 
/// Subscription: Punkouter26 (Bbb8dfbe-9169-432f-9b7a-fbf861b51037)
/// </summary>
public static class AzureManagedIdentityExtensions
{
    /// <summary>
    /// Subscription ID for the Punkouter26 subscription.
    /// </summary>
    public const string SubscriptionId = "Bbb8dfbe-9169-432f-9b7a-fbf861b51037";

    /// <summary>
    /// Resource group for PoShared resources (App Service Plans, Key Vault, etc.)
    /// </summary>
    public const string PoSharedResourceGroup = "rg-poshared";

    /// <summary>
    /// Resource group for PoTraffic application resources (App Service, Storage, etc.)
    /// </summary>
    public const string PoTrafficResourceGroup = "rg-potraffic";

    /// <summary>
    /// Creates a DefaultAzureCredential configured for the Punkouter26 subscription.
    /// </summary>
    public static TokenCredential CreateManagedIdentityCredential(IConfiguration configuration)
    {
        // Check if we should exclude Managed Identity (for local development)
        bool excludeManagedIdentity = configuration.GetValue<bool>("AzureCredential:ExcludeManagedIdentityCredential");
        
        if (excludeManagedIdentity)
        {
            // Local development: use Visual Studio, Azure CLI, etc.
            return new DefaultAzureCredential(new DefaultAzureCredentialOptions
            {
                ExcludeManagedIdentityCredential = true,
                ExcludeEnvironmentCredential = false,
                ExcludeVisualStudioCredential = true, // Set to false if using VS
                ExcludeAzureCliCredential = true,        // Set to false if using az cli
                ExcludeAzurePowerShellCredential = true,
                ExcludeInteractiveBrowserCredential = true
            });
        }

        // Production: use Managed Identity with fallback
        return new DefaultAzureCredential(new DefaultAzureCredentialOptions
        {
            ExcludeManagedIdentityCredential = false,
            ExcludeEnvironmentCredential = false,
            ExcludeVisualStudioCredential = true,
            ExcludeAzureCliCredential = false,
            ExcludeAzurePowerShellCredential = false,
            ExcludeInteractiveBrowserCredential = true
        });
    }

    /// <summary>
    /// Gets the Azure Key Vault URI from configuration.
    /// Falls back to PoShared key vault in production.
    /// </summary>
    public static string GetKeyVaultUri(IConfiguration configuration)
    {
        string? configuredUri = configuration["AzureKeyVault:VaultUri"];
        
        if (!string.IsNullOrWhiteSpace(configuredUri))
        {
            return configuredUri;
        }

        // Production fallback - PoShared Key Vault
        return "https://kv-poshared.vault.azure.net/";
    }

    /// <summary>
    /// Gets the App Insights connection string from configuration or Key Vault.
    /// </summary>
    public static string GetAppInsightsConnectionString(IConfiguration configuration)
    {
        return configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]
            ?? configuration["AppInsights:ConnectionString"]
            ?? throw new InvalidOperationException("App Insights connection string is not configured.");
    }
}

/// <summary>
/// Constants for Azure resource names and endpoints.
/// </summary>
public static class AzureResourceConstants
{
    // App Service
    public const string AppServiceName = "app-potraffic";
    public const string AppServicePlanName = "asp-poshared";
    
    // Storage
    public const string StorageAccountName = "potraffic";
    public const string TableStorageEndpoint = "https://potraffic.table.core.windows.net/";
    
    // Key Vault
    public const string KeyVaultName = "kv-poshared";
    
    // App Insights
    public const string AppInsightsName = "appi-poshared";
}
