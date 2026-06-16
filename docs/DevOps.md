# PoTraffic DevOps & Deployment Strategy

## Tech Stack

- **Hosting**: Azure App Service
- **Storage**: Azure Table Storage, Azurite locally
- **Job Processing**: Table Storage-backed scheduler
- **Secrets**: Managed Identity + `kv-poshared`
- **Logging**: Serilog + Azure Monitor / Application Insights

## CI/CD Pipeline

1. Restore, build, and run unit tests.
2. Start Azurite and run integration tests.
3. Start the hosted API/client and run Playwright E2E tests.
4. Deploy to Azure App Service through `azd`.

## Environment Secrets

| Key | Purpose |
|---|---|
| `PoTraffic--Jwt--Key` | JWT signing key |
| `PoTraffic--ConnectionStrings--TableStorage` | Azure Table Storage access |
| `PoTraffic--ExternalAuth--Microsoft--ClientId` | Microsoft OAuth client |
| `PoTraffic--ExternalAuth--Microsoft--ClientSecret` | Microsoft OAuth secret |
| `PoTraffic--GoogleMaps--ApiKey` | Google Maps provider access |
| `PoTraffic--TomTom--ApiKey` | TomTom provider access |
