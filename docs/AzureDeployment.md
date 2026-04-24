# PoTraffic Azure Deployment Guide

## Overview

This document describes the Azure deployment configuration for PoTraffic application.

## Azure Resources

### Subscription
- **Subscription Name**: Punkouter26
- **Subscription ID**: `Bbb8dfbe-9169-432f-9b7a-fbf861b51037`

### Resource Groups

| Resource Group | Purpose | Shared Resources |
|----------------|---------|-----------------|
| `rg-poshared` | Shared infrastructure | App Service Plans, Key Vault, App Insights |
| `rg-potraffic` | PoTraffic application | App Service, Table Storage |

### Resource Configuration

#### rg-poshared (Shared Resources)
| Resource | Name | Type | Notes |
|----------|------|------|-------|
| Key Vault | `kv-poshared` | Key Vault | Stores all secrets |
| App Service Plan | `asp-poshared` | App Service Plan | Shared by Po* apps |
| App Insights | `appi-poshared` | Application Insights | Centralized telemetry |

#### rg-potraffic (Application Resources)
| Resource | Name | Type | Notes |
|----------|------|------|-------|
| App Service | `app-potraffic` | App Service | Main application host |
| Table Storage | `potraffic` | Storage Account | Traffic poll data |

## Identity & Authentication

### Managed Identity
- Uses `DefaultAzureCredential` with Managed Identity fallback
- Authentication order:
  1. Managed Identity (Azure App Service, VM, Container Apps)
  2. Visual Studio credentials
  3. Azure CLI credentials
  4. Azure PowerShell credentials

### Secret Management
All secrets are stored in Azure Key Vault (`kv-poshared`):
- **PoTraffic-prefixed secrets**: `PoTraffic--ConnectionStrings--Default`, etc.
- **Shared secrets**: Stored without prefix
- PrefixKeyVaultSecretManager strips the prefix when loading

## App Service Configuration

### Settings (from Key Vault)
```json
{
  "AzureKeyVault:VaultUri": "https://kv-poshared.vault.azure.net/",
  "Features:EnableAiFeatures": true,
  "Features:EnableExternalTrafficProviders": true,
  "ConnectionStrings:Default": "Server=...;Database=PoTraffic;...",
  "ConnectionStrings:TableStorage": "UseDevelopmentStorage=true"
}
```

### Environment Variables
```
ASPNETCORE_ENVIRONMENT=Production
AzureCredential__ExcludeManagedIdentityCredential=false
```

## Table Storage Configuration

### Local Development (Azurite)
```json
"ConnectionStrings": {
  "TableStorage": "UseDevelopmentStorage=true"
}
```

Run Azurite in Docker:
```bash
docker run --rm -p 10000:10000 -p 10001:10001 mcr.microsoft.com/azure-storage/azurite
```

### Production
Uses Managed Identity to access `potraffic` storage account in `rg-potraffic`.

## Deployment Steps

### Prerequisites
1. Azure CLI authenticated: `az login`
2. Appropriate Azure role assignments for the deployment service principal
3. Key Vault access policies configured

### Deploy Infrastructure (if needed)
```bash
# Create resource groups if not exist
az group create --name rg-poshared --location eastus
az group create --name rg-potraffic --location eastus

# Create resources (one-time setup)
az appservice plan create --name asp-poshared --resource-group rg-poshared --sku B1 --is-linux
az webapp create --name app-potraffic --resource-group rg-potraffic --plan asp-poshared --runtime "DOTNET|10"

# Enable Managed Identity on App Service
az webapp identity assign --name app-potraffic --resource-group rg-potraffic

# Grant Key Vault access
az keyvault set-policy --name kv-poshared --object-id <managed-identity-object-id> --secret-permissions get list
```

### Deploy Application
```bash
# Build and publish
dotnet publish src/PoTraffic.Api/PoTraffic.Api.csproj --configuration Release --output ./publish

# Deploy to App Service
az webapp up --name app-potraffic --resource-group rg-potraffic --runtime "DOTNET|10"
```

Or use Azure DevOps/GitHub Actions with appropriate service connections.

## Key Vault Secret Structure

### App-Specific Secrets (prefixed with PoTraffic--)
```
PoTraffic--ConnectionStrings--Default
PoTraffic--Jwt--Key
PoTraffic--ExternalAuth--Google--ClientId
PoTraffic--ExternalAuth--Google--ClientSecret
PoTraffic--GoogleMaps--ApiKey
```

### Shared Secrets (no prefix)
```
SqlServer--ConnectionString
ApplicationInsights--InstrumentationKey
```

## Observability

### Serilog Sinks
- **Console**: Development logging
- **File**: Rolling log files in App Service
- **App Insights**: Structured logs to `appi-poshared`

### OpenTelemetry
- Instrumented automatically via `Azure.Monitor.OpenTelemetry.Exporter`
- Traces and metrics aggregated to `appi-poshared`

### Health Checks
- `/health` endpoint returns JSON status
- `/diag` page available in Development only

## Rollback Procedure

If deployment fails:
```bash
az webapp deployment slot swap --name app-potraffic --resource-group rg-potraffic --slot staging
```

Or redeploy previous version from App Service deployment center.
