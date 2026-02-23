# PoTraffic — Empirical Commute Volatility Engine

PoTraffic is a Blazor WebAssembly application that empirically measures commute route volatility using Google Maps and TomTom APIs. It records travel-time samples on a schedule, computes baseline mean and standard deviation per route, and flags anomalies (congestion, rerouting) in real time — giving commuters data-driven insight into their journey reliability.

## Architecture

| Layer | Technology |
|---|---|
| Front-end | Blazor WebAssembly (.NET 10) + Radzen Blazor |
| Back-end | ASP.NET Core Minimal API (.NET 10) + MediatR (CQRS) |
| Persistence | Entity Framework Core (Code-First) + SQL Server |
| Background Jobs | Hangfire (recursive polling chains + nightly pruning) |
| Auth | ASP.NET Core Identity + JWT bearer |
| Logging | Serilog (structured) + WASM client log forwarding |
| Observability | OpenTelemetry SDK + Azure Monitor exporter |
| Testing | xUnit + NSubstitute (unit), Testcontainers (integration), Playwright .NET (E2E) |

The API and Blazor WASM static files are hosted together on a single **Azure App Service**.

## Project Structure

```
src/
  PoTraffic.Api/              # ASP.NET Core Minimal API — features as vertical slices
  PoTraffic.Client/           # Blazor WASM front-end
  PoTraffic.Shared/           # DTOs shared between API and client
tests/
  PoTraffic.UnitTests/        # xUnit + NSubstitute — isolated business logic
  PoTraffic.IntegrationTests/ # xUnit + WebApplicationFactory + Testcontainers
  PoTraffic.E2ETests/         # Playwright .NET — critical user journeys
specs/
  00001-potraffic-core/       # Feature specs, plan, data model, API contracts
```

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Docker Desktop](https://www.docker.com/products/docker-desktop/) (for SQL Server + Testcontainers)
- [Azure CLI](https://learn.microsoft.com/cli/azure/install-azure-cli) (`az`) — for Key Vault local dev

## Quick Start

### 1. Clone & Restore

```bash
git clone <repo-url>
cd PoTraffic
dotnet restore
```

### 2. Run Local Infrastructure (.NET Aspire)

PoTraffic uses **.NET Aspire** to orchestrate local SQL Server (Azure SQL Edge) and dashboarding services:

```bash
dotnet run --project src/PoTraffic.AppHost
```

Navigate to the Aspire dashboard (URL printed in terminal) to monitor the local stack.

### 3. Run the API (standalone)

If you prefer to run the API directly without the Aspire orchestrator (e.g., for E2E testing or debugging):

```bash
dotnet run --project src/PoTraffic.Api --launch-profile http
```

### 3. Configure Connection String

```bash
dotnet user-secrets set "ConnectionStrings:Default" \
  "Server=localhost,1433;Database=PoTraffic;User=sa;Password=Dev!Passw0rd;TrustServerCertificate=true" \
  --project src/PoTraffic.Api
```

### 4. Apply Migrations

```bash
dotnet ef database update \
  --project src/PoTraffic.Api \
  --startup-project src/PoTraffic.Api
```

This creates all tables and seeds:
- `SystemConfiguration` rows (poll costs, daily quota defaults)
- `PublicHoliday` rows for `en-IE`, `en-GB`, `de-DE`, `fr-FR`, `en-US` (2025)

### 5. Run the API

```bash
dotnet run --project src/PoTraffic.Api
# Hot reload: dotnet watch --project src/PoTraffic.Api
```

| URL | Purpose |
|---|---|
| `https://localhost:7080` | API base |
| `https://localhost:7080/hangfire` | Hangfire dashboard (admin-only in production) |
| `https://localhost:7080/swagger` | OpenAPI (Development only) |
| `https://localhost:7080/health` | Health check |

### 6. Run the Blazor Client

In a second terminal:

```bash
dotnet run --project src/PoTraffic.Client
# Default: https://localhost:7081
```

> In production the client static files are published to `src/PoTraffic.Api/wwwroot` and served by the API project directly.

## Running Tests

### Unit Tests

```bash
dotnet test tests/PoTraffic.UnitTests
```

### Integration Tests (requires Docker)

```bash
dotnet test tests/PoTraffic.IntegrationTests
```

SQL Edge container starts automatically via Testcontainers. First run takes ~120 seconds; subsequent runs use cached layers (~30 seconds).

### E2E Tests (Playwright)

```bash
# Terminal 1 — API in Testing mode (activates /e2e/dev-login and /e2e/seed)
ASPNETCORE_ENVIRONMENT=Testing dotnet run --project src/PoTraffic.Api

# Terminal 2 — Install Chromium (first time only)
pwsh tests/PoTraffic.E2ETests/playwright.ps1 install chromium

# Terminal 3 — Run E2E suite
dotnet test tests/PoTraffic.E2ETests
```

## Key Concepts

### Monitoring Engine

Routes have **monitoring windows** (e.g., Mon–Fri 08:00–09:00). When a window is active, Hangfire executes a recursive polling chain: each poll schedules the next one 5 minutes later, until the window closes. Each poll calls the configured provider (Google Maps or TomTom) and records duration, distance, and a reroute flag.

### Baseline & Volatility

After ≥3 sessions on the same weekday, the engine computes a **baseline** (mean ± σ) per 5-minute time slot. Readings that deviate by more than 1σ are flagged as anomalous. The admin **Global Volatility** page shows cross-user STDDEV heatmaps by day-of-week and time slot.

### Holiday Exclusion

Sessions on public holidays are flagged `IsHolidayExcluded = true` and omitted from the STDDEV baseline, preventing holiday traffic patterns from skewing the commute baseline.

### Daily Quota

Each user has a configurable daily monitoring-session quota (default: 10). Exceeding the quota returns `429 Too Many Requests`. Quota resets at `00:00 UTC`.

### GDPR Hard Delete

`DELETE /api/account` permanently removes the authenticated user and all associated data (routes, sessions, poll records) via EF Core cascade delete.

## Development Guidelines

This project follows [Spec-Driven Development (SDD)](AGENTS.md) with a locked technology stack. Key constraints:

- **No horizontal `Services/`, `Repositories/`, `DTOs/` folders** — features are vertical slices under `Features/<FeatureName>/`
- **MediatR** for all in-process request dispatching; **FluentValidation** for input validation
- **Radzen Blazor** for all UI controls beyond basic HTML
- **Zero dead code** — unused files, commented-out blocks, and obsolete assets are removed before merge
- All PRs must ship **unit + integration + E2E tests** for new features

See [`.github/copilot-instructions.md`](.github/copilot-instructions.md) for the full project principles.

## Troubleshooting

| Symptom | Likely Cause | Fix |
|---|---|---|
| API startup hangs 3–5 s | Managed Identity probe | Add `"ExcludeManagedIdentityCredential": true` in `appsettings.Development.json` |
| SQL Edge not ready | ARM64 QEMU startup time | Wait 90–120 s; check `docker logs potraffic-sqlserver-1` |
| Hangfire jobs not executing | Server not started | Ensure `app.UseHangfireServer()` is called in `Program.cs` |
| Testcontainers timeout | Container startup > 30 s | Set `ContainerStartupTimeout = TimeSpan.FromSeconds(120)` |
| `/e2e/dev-login` returns 404 | Expected, non-Testing env | Only registered when `ASPNETCORE_ENVIRONMENT=Testing` |
| Chart not updating | Blazor in-place mutation | Replace collection reference (new `List<T>`); do not mutate in-place |
| STDDEV query returns null | Fewer than 3 sessions | Show "building baseline" UI state |

## License

See `LICENSE` for details.
