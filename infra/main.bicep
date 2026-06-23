// PoTraffic — App Service configuration as code.
// Captures the production web app's settings, identity, plan binding, and TLS/HTTPS
// posture so they are reproducible and drift-proof (these were previously set by hand).
//
// IMPORTANT: `appSettings` below is AUTHORITATIVE — applying replaces the live set.
// Always preview before applying:
//   az deployment group what-if -g PoTraffic -f infra/main.bicep -p @infra/main.parameters.json
//   az deployment group create   -g PoTraffic -f infra/main.bicep -p @infra/main.parameters.json
//
// The App Service Plan, user-assigned managed identity, and Key Vault live in the
// shared PoShared resource group and are referenced here by resource ID (not managed
// by this template).

targetScope = 'resourceGroup'

@description('App Service (web app) name.')
param webAppName string = 'app-potraffic-api-prod-wus2-001'

@description('Region — MUST match the App Service Plan region.')
param location string = 'westus2'

@description('Resource ID of the shared App Service Plan in PoShared (Basic+ for Always On).')
param appServicePlanId string

@description('Resource ID of the shared user-assigned managed identity (PoShared).')
param sharedIdentityId string

@description('Client ID of the shared managed identity — DefaultAzureCredential targets this.')
param sharedIdentityClientId string

@description('Key Vault URI (config key KeyVault:Uri, read at startup).')
param keyVaultUri string = 'https://kv-platform-prod-eus2-00.vault.azure.net/'

resource web 'Microsoft.Web/sites@2024-04-01' = {
  name: webAppName
  location: location
  kind: 'app,linux'
  identity: {
    // Shared user-assigned MI is the one DefaultAzureCredential uses (via AZURE_CLIENT_ID).
    type: 'SystemAssigned, UserAssigned'
    userAssignedIdentities: {
      '${sharedIdentityId}': {}
    }
  }
  properties: {
    serverFarmId: appServicePlanId
    httpsOnly: true
    siteConfig: {
      linuxFxVersion: 'DOTNETCORE|10.0'
      alwaysOn: true
      ftpsState: 'FtpsOnly'
      minTlsVersion: '1.2'
      appSettings: [
        { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
        // NOTE: config key is KeyVault:Uri — the setting MUST be 'KeyVault__Uri'
        // (a misnamed 'AzureKeyVault__VaultUri' silently disables Key Vault → 503).
        { name: 'KeyVault__Uri', value: keyVaultUri }
        { name: 'AZURE_CLIENT_ID', value: sharedIdentityClientId }
        // Deploy a pre-built artifact from CI — skip Oryx build-on-deploy.
        { name: 'SCM_DO_BUILD_DURING_DEPLOYMENT', value: 'false' }
      ]
    }
  }
}

output defaultHostName string = web.properties.defaultHostName
output principalId string = web.identity.principalId
