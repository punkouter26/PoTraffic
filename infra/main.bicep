// PoTraffic — App Service configuration as code.
// CI/CD rule #6: Po naming convention, PoShared resource group, Managed Identity only.
//
// Naming convention enforced:
//   resourceGroup        = PoTraffic
//   appService (web app) = app-potraffic-{app}-{env}-{region}-{seq} (target shape). Current
//                          deployment is `potraffic-api-win` (legacy, retained for continuity);
//                          new environments should adopt the target shape.
//   Key Vault            = kv-poshared                                (in PoShared RG, reused across all Po* apps)
//   Managed Identity     = mi-poshared-*                              (in PoShared RG)
//
// No connection strings anywhere. All secrets come from Key Vault via
// DefaultAzureCredential → user-assigned MI → Key Vault Secrets User role.

targetScope = 'resourceGroup'

@description('App Service (web app) name. MUST follow app-potraffic-api-{env}-{region}-{seq}.')
@minLength(10)
@maxLength(60)
param webAppName string = 'app-potraffic-api-prod-wus2-001'

@description('Region — MUST match the App Service Plan region.')
param location string = 'westus2'

@description('Resource ID of the shared App Service Plan in PoShared (Basic+ for Always On).')
param appServicePlanId string

@description('Resource ID of the shared user-assigned managed identity (PoShared).')
param sharedIdentityId string

@description('Client ID of the shared managed identity — DefaultAzureCredential targets this.')
param sharedIdentityClientId string

@description('Key Vault URI in PoShared. PoTraffic reads all secrets from here — no connection strings.')
param keyVaultUri string = 'https://kv-poshared.vault.azure.net/'

@description('Key Vault name, derived from URI, for role assignment scope.')
param keyVaultName string = last(split(keyVaultUri, '/'))

@description('Storage account for PoTraffic (table/queue/blob). Set to empty string to skip provisioning.')
param storageAccountName string = 'stpotrafficprodwus2001'

@description('Enable 30-day blob lifecycle policy on the PoTraffic storage account (CI/CD rule #8).')
param enableStorageLifecycle bool = true

resource web 'Microsoft.Web/sites@2024-04-01' = {
  name: webAppName
  location: location
  kind: 'app,linux'
  identity: {
    // System-assigned MI for app-internal Azure resource access.
    // User-assigned MI from PoShared is the one DefaultAzureCredential uses
    // (via AZURE_CLIENT_ID) to read Key Vault.
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
        // Surface the real startup exception instead of a generic 500.30.
        // Stripped back to false once ops confirms healthy boots.
        { name: 'ASPNETCORE_DETAILEDERRORS', value: 'true' }
        // MEL/Sink verbosity — captures hydration, scheduler, and storage failures.
        { name: 'Logging__LogLevel__Default', value: 'Information' }
        { name: 'Logging__LogLevel__Microsoft__Hosting__Lifetime', value: 'Information' }
        { name: 'Logging__LogLevel__PoTraffic', value: 'Debug' }
        // Force the managed-identity path explicitly (TableStorageExtensions
        // honors this and avoids DefaultAzureCredential ambiguity).
        { name: 'AzureTable__UseManagedIdentity', value: 'true' }
      ]
    }
  }
}

// ── Role assignment: Key Vault Secrets User for the system-assigned identity ────
// CI/CD rule #6: Managed Identity only. The web app's system-assigned identity
// gets 'Key Vault Secrets User' (a data-plane read-only role) on the shared KV.
resource kv 'Microsoft.KeyVault/vaults@2023-07-01' existing = {
  name: keyVaultName
}

resource webKvSecretsUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(kv.id, web.id, 'Key Vault Secrets User')
  scope: kv
  properties: {
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions',
      '4633458b-17de-408a-b874-0445c86b69e6')  // Key Vault Secrets User
    principalId: web.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

// ── CI/CD rule #8 — Storage lifecycle: auto-delete after 30 days ────────────
// Provisioned only if storageAccountName is non-empty AND enableStorageLifecycle.
resource storage 'Microsoft.Storage/storageAccounts@2023-01-01' = if (!empty(storageAccountName)) {
  name: storageAccountName
  location: location
  sku: {
    name: 'Standard_LRS'
  }
  kind: 'StorageV2'
  properties: {
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
    allowBlobPublicAccess: false
    networkAcls: {
      defaultAction: 'Allow'
    }
  }
}

// ── Storage role assignments (CI/CD rule #6 — Managed Identity only) ───────
//
// Grant 'Storage Table Data Contributor' to BOTH the user-assigned MI
// (sharedIdentityId — read via AZURE_CLIENT_ID at runtime) and the system-
// assigned MI on the App Service (web.identity.principalId — the one the
// Portal IAM selector targets). Without either, HydrateAsync throws a 403
// and the host crashes with HTTP 500.30.
//
// The user-assigned MI reference uses an explicit existing-resource lookup so
// Bicep correctly resolves principalId at deploy time.
resource sharedIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' existing = {
  name: last(split(sharedIdentityId, '/'))
}

var storageTableDataContributorRoleId = subscriptionResourceId(
  'Microsoft.Authorization/roleDefinitions',
  '0a9a7e1f-b9d0-4cc4-a60d-9918e0c33e7d')  // Storage Table Data Contributor

resource saUserAssignedTableContrib 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(storageAccountName)) {
  name: guid(storage.id, sharedIdentityId, 'sttdc')
  scope: storage
  properties: {
    roleDefinitionId: storageTableDataContributorRoleId
    principalId:      sharedIdentity.properties.principalId
    principalType:    'ServicePrincipal'
  }
}

resource saSystemAssignedTableContrib 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(storageAccountName)) {
  name: guid(storage.id, web.id, 'sttdc')
  scope: storage
  properties: {
    roleDefinitionId: storageTableDataContributorRoleId
    principalId:      web.identity.principalId
    principalType:    'ServicePrincipal'
  }
}

// Management policy: archive after 30 days, then delete after 365 days (CI/CD rule #8).
resource storageLifecycle 'Microsoft.Storage/storageAccounts/managementPolicies@2023-01-01' = if (!empty(storageAccountName) && enableStorageLifecycle) {
  parent: storage
  name: 'default'
  properties: {
    policy: {
      rules: [
        {
          name: 'PoTrafficArchiveAfter30Days'
          enabled: true
          type: 'Lifecycle'
          definition: {
            actions: {
              baseBlob: {
                tierToArchive: {
                  daysAfterModificationGreaterThan: 30
                }
                delete: {
                  daysAfterModificationGreaterThan: 365
                }
              }
            }
            filters: {
              blobTypes: [ 'blockBlob' ]
            }
          }
        }
      ]
    }
  }
}

output defaultHostName string = web.properties.defaultHostName
output principalId string = web.identity.principalId
output kvRoleAssignmentId string = webKvSecretsUser.id
output poNamingCompliant bool = startsWith(webAppName, 'app-potraffic-')
