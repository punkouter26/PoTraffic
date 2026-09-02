# Infrastructure (IaC)

`main.bicep` captures the **production App Service configuration** for `potraffic-api-win`
so it is reproducible and drift-proof. The App Service Plan, shared user-assigned
managed identity, and Key Vault live in the **PoShared** resource group and are
referenced by ID (not created here).

## Apply

Always preview first — `appSettings` in the template is authoritative and replaces
the live set:

```bash
az deployment group what-if -g PoTraffic -f infra/main.bicep -p @infra/main.parameters.json
az deployment group create   -g PoTraffic -f infra/main.bicep -p @infra/main.parameters.json
```

## What it encodes (and why)

| Setting | Value | Why |
|---|---|---|
| `KeyVault__Uri` | `https://kv-poshared.vault.azure.net/` | Config key is `KeyVault:Uri`. A misnamed `AzureKeyVault__VaultUri` silently disables Key Vault → prod fail-fast → 503. |
| `AZURE_CLIENT_ID` | shared MI clientId | Makes `DefaultAzureCredential` use the shared `mi-poshared-containerapps` identity for Key Vault. |
| `ASPNETCORE_ENVIRONMENT` | `Production` | Prod hard-requires Key Vault secrets. |
| `SCM_DO_BUILD_DURING_DEPLOYMENT` | `false` | CI deploys a pre-built artifact; skip Oryx build-on-deploy. |
| `httpsOnly` / `minTlsVersion` / `ftpsState` | true / 1.2 / FtpsOnly | Security posture. |
| `alwaysOn` | true | Requires Basic+ plan; eliminates cold-start 504s. |
| `serverFarmId` | `asp-poissues-b1-wus2` (B1, PoShared) | Dedicated CPU; zero added cost (Basic bills per-plan). |

Secrets are **never** in here — they live in Key Vault namespaced `PoTraffic--*`
and are loaded by `PrefixKeyVaultSecretManager`.
