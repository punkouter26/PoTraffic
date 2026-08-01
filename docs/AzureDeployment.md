# PoTraffic Azure Deployment Guide

## Overview

PoTraffic deploys to Azure App Service. The API hosts the Blazor WASM client,
serves Minimal API endpoints, and persists operational data in Azure Table
Storage. Production secrets are loaded from the shared Po Key Vault.

## Azure Resources

| Resource Group | Purpose | Resources |
|---|---|---|
| `rg-poshared` | Shared Po infrastructure | App Service Plan, `kv-poshared`, `appi-poshared` |
| `rg-potraffic` | PoTraffic application | App Service, Storage Account |

## Identity & Secrets

- Azure App Service uses Managed Identity in production.
- Key Vault is `kv-poshared` in `rg-poshared`.
- PoTraffic-specific secrets use the `PoTraffic--` prefix, mapped to `:` by
  `PrefixKeyVaultSecretManager`.

Common secret names:

```text
PoTraffic--Jwt--Key
PoTraffic--ConnectionStrings--TableStorage
PoTraffic--ExternalAuth--Microsoft--ClientId
PoTraffic--ExternalAuth--Microsoft--ClientSecret
PoTraffic--GoogleMaps--ApiKey
PoTraffic--TomTom--ApiKey
PoTraffic--ApplicationInsights--ConnectionString
```

## Storage

Local development uses Azurite through `docker-compose.yml`:

```powershell
docker compose up -d
```

Production uses Azure Table Storage with Managed Identity or a Key Vault-provided
connection string.

## Deployment

Use `azd` from the repository root:

```powershell
az login
azd up
```

`azure.yaml` maps `src/PoTraffic.API` to an Azure App Service host. The API
serves the WASM client directly; do not configure CORS.

## Observability

- Serilog writes structured logs to console/file sinks.
- OpenTelemetry records ASP.NET Core inbound requests and outbound HTTP calls.
- Azure Monitor export is enabled when an App Insights connection string is
  resolved from configuration or Key Vault.
- `/health/json` returns JSON for uptime checks; `/health` is the Blazor status page.
- `/diag` is a hidden diagnostic page with masked configuration values.
