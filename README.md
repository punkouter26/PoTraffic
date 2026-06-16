# PoTraffic - Empirical Commute Volatility Engine

PoTraffic is a Blazor WebAssembly + ASP.NET Core application that measures
commute route volatility using Google Maps and TomTom APIs. It records
travel-time samples on a schedule, computes route baselines, and flags
congestion or reroute anomalies in real time.

## Architecture

| Layer | Technology |
|---|---|
| Front-end | Blazor WebAssembly (.NET 10) + Radzen Blazor |
| Back-end | ASP.NET Core Minimal API (.NET 10) + MediatR |
| Persistence | Azure Table Storage, Azurite locally |
| Background Jobs | Table Storage-backed scheduler |
| Auth | Microsoft OAuth in dev/prod, Testing-only auth bypasses |
| Logging | Serilog + WASM client log forwarding |
| Observability | OpenTelemetry + Azure Monitor / Application Insights |
| Testing | xUnit + NSubstitute, integration tests, Playwright .NET E2E |

The API and Blazor WASM static files are hosted together on Azure App Service.
CORS is intentionally not configured.

## Project Structure

```text
src/
  PoTraffic.Domain/          # Pure domain entities and value objects
  PoTraffic.Application/     # Interfaces, contracts, validators
  PoTraffic.Infrastructure/  # Azure, Table Storage, external providers
  PoTraffic.Api/             # ASP.NET Core host and vertical slices
  PoTraffic.Client/          # Blazor WASM front-end
  PoTraffic.Shared/          # DTOs shared by API and client
tests/
  PoTraffic.UnitTests/
  PoTraffic.IntegrationTests/
  PoTraffic.E2ETests/
```

## Prerequisites

- .NET 10 SDK
- Docker Desktop for Azurite
- Azure CLI for local Key Vault access

## Quick Start

```powershell
dotnet restore
dotnet build
./SCRIPTS/start-dev.ps1
```

The startup script starts Azurite, clears stale `dotnet` processes on ports
5000/5001, and launches the hosted API/client.

| URL | Purpose |
|---|---|
| `https://localhost:5001` | Hosted API + Blazor client |
| `https://localhost:5001/scalar/v1` | Scalar API reference in Development |
| `https://localhost:5001/health` | JSON health check |
| `https://localhost:5001/diag` | Hidden diagnostics page |

## Running Tests

```powershell
./SCRIPTS/run-tests.ps1
```

Or run individual suites:

```powershell
dotnet test tests/PoTraffic.UnitTests
dotnet test tests/PoTraffic.IntegrationTests
dotnet test tests/PoTraffic.E2ETests
```

## Key Concepts

Routes have monitoring windows. When a window is active, the scheduler records
traffic samples at configured intervals until the window closes. Samples are
stored in Table Storage and used to build baseline travel-time bands per route
and time slot.

Development and Production require Microsoft OAuth. Testing exposes isolated
auth bypass endpoints for integration and E2E tests; these are not available
when running the app normally.

## Development Guidelines

- Keep features as vertical slices under `Features/<FeatureName>/`.
- Do not add root-level `Services/`, `Repositories/`, or `DTOs/` folders.
- Keep NuGet versions in `Directory.Packages.props`.
- Do not enable AOT or trimming.
- Remove dead code and stale docs before merge.

See [AGENTS.md](AGENTS.md) for the full coding-agent contract.
