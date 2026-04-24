# PoTraffic LLM Documentation

> **Note**: Update this file only when project structure or public API surfaces change significantly.

This folder contains documentation for AI coding assistants to quickly understand the PoTraffic codebase.

## Project Overview

**PoTraffic** is a traffic monitoring application that polls traffic data from external providers (Google Maps, TomTom) and stores results for analysis. It uses a Blazor WASM frontend with an ASP.NET Core API backend.

## Quick Start

### Running Locally
1. Start Azurite: `docker run --rm -p 10000:10000 -p 10001:10001 mcr.microsoft.com/azure-storage/azurite`
2. Start SQL Server (or use connection string in appsettings.Development.json)
3. Press F5 in VS Code (API Development profile) or run `dotnet run --project src/PoTraffic.Api`

### Ports
- HTTP: `http://localhost:5000`
- HTTPS: `https://localhost:5001`
- Client (WASM): `http://localhost:5259`

### Default Credentials
- Admin: `admin@potraffic.dev` / `Admin123!`
- ANON (Dev only): Use the "🔓 ANON" button on login page

## Architecture

### Solution Structure
```
PoTraffic.slnx
├── src/
│   ├── PoTraffic.Api/          # ASP.NET Core API (Onion Architecture)
│   ├── PoTraffic.Client/       # Blazor WASM frontend
│   └── PoTraffic.Shared/       # Shared DTOs, Enums, Constants
└── tests/
    ├── PoTraffic.UnitTests/     # Domain/Service layer tests
    ├── PoTraffic.IntegrationTests/ # API/Repository tests (Testcontainers)
    └── PoTraffic.E2ETests/      # Playwright UI tests
```

### Onion Architecture (API Layer)
```
PoTraffic.Api/
├── Features/           # Use Cases (CQRS with MediatR)
│   ├── Account/        # User account management
│   ├── Admin/          # Admin operations
│   ├── Auth/           # Authentication (JWT + OAuth)
│   ├── Config/         # System configuration
│   ├── History/        # Historical data queries
│   ├── Maintenance/    # Background jobs
│   ├── MonitoringWindows/ # Time window management
│   └── Routes/         # Route monitoring
├── Infrastructure/     # Cross-cutting concerns
│   ├── Data/           # EF Core DbContext, Migrations
│   ├── Hangfire/       # Background job scheduling
│   ├── Logging/        # Serilog configuration
│   ├── Observability/  # OpenTelemetry setup
│   ├── Providers/      # Traffic provider abstraction
│   ├── Security/       # JWT, Claims, Auth
│   └── Testing/        # Mock providers for testing
└── Program.cs          # Application startup
```

### Key Patterns Used
- **CQRS**: MediatR handlers in Features folders
- **Pipeline Behavior**: ValidationBehavior (FluentValidation)
- **Decorator Pattern**: HangfireAdminAuthorizationFilter
- **Factory Pattern**: KeyedServiceTrafficProviderFactory
- **Template Method**: Hangfire recurring jobs

## Technology Stack

| Component | Technology |
|-----------|------------|
| Framework | .NET 10 |
| Frontend | Blazor WASM + Radzen UI |
| Database | SQL Server (EF Core) |
| Background Jobs | Hangfire |
| Auth | JWT Bearer + OAuth (Google, Microsoft) |
| Logging | Serilog → Console, File, App Insights |
| Observability | OpenTelemetry → App Insights |
| API Docs | Scalar (OpenAPI) |

## Configuration

### Ports (Development)
- HTTP: `http://localhost:5000`
- HTTPS: `https://localhost:5001`

### Environment Variables
- `ASPNETCORE_ENVIRONMENT`: Development | Testing | Production

### Key Vault
Secrets are prefixed with `PoTraffic--` and stripped by `PrefixKeyVaultSecretManager`.

## Database

### Entity Framework Core
- Context: `PoTrafficDbContext`
- Migrations: Applied automatically on startup
- Default Admin: `admin@potraffic.dev` / `Admin123!`

## Testing

### Unit Tests
- Target Domain logic and Service layers
- Use NSubstitute for mocking
- Use FluentAssertions for assertions

### Integration Tests
- Use Testcontainers.MsSql for SQL Server
- Use WebApplicationFactory<Program>
- Mock external providers via `MockTrafficProvider`

### E2E Tests
- Playwright (TypeScript-style in C#)
- Headed mode in Development
- Focus on critical user paths

## External Providers

### Traffic Providers
- `ITrafficProvider` interface
- Implementations: `GoogleMapsTrafficProvider`, `TomTomTrafficProvider`
- Factory: `KeyedServiceTrafficProviderFactory`

### Mock Mode
In Testing environment, use `MockTrafficProvider` for deterministic responses.

## Feature Flags

Feature flags are defined in `appSettings.json` under `FeatureFlags`:
```json
{
  "FeatureFlags": {
    "EnableAiFeatures": true,
    "EnableExternalTrafficProviders": true
  }
}
```

## Important Notes

1. **No secrets in appsettings.json** - All secrets from Azure Key Vault
2. **wwwroot only in Client project** - Server project should not have wwwroot
3. **Azurite for local storage** - Use Docker Azurite for Table Storage emulation
4. **ANON login** - Development bypass for OAuth testing
