# Quickstart Guide: PoTraffic Local Development

**Feature**: PoTraffic — Empirical Commute Volatility Engine  
**Branch**: `00001-potraffic-core`  
**Prerequisites**: .NET 10 SDK, Docker Desktop (ARM64 or x86_64), Azure CLI (`az`)

---

## 1. Clone & Bootstrap

```bash
git clone <repo-url>
cd PoTraffic
dotnet restore
```

---

## 2. Infrastructure: Docker Compose

Start Azure SQL Edge (ARM64-compatible) and Azurite (local Azure Blob/Queue emulation) via Docker:

```yaml
# docker-compose.yml (root of repo)
services:
  sqlserver:
    image: mcr.microsoft.com/azure-sql-edge:latest
    platform: linux/amd64       # runs under QEMU on ARM64
    environment:
      ACCEPT_EULA: "1"
      MSSQL_SA_PASSWORD: "Dev!Passw0rd"   # min 8 chars, mixed complexity
    ports:
      - "1433:1433"
    volumes:
      - sqldata:/var/opt/mssql

  azurite:
    image: mcr.microsoft.com/azure-storage/azurite:latest
    ports:
      - "10000:10000"
      - "10001:10001"
      - "10002:10002"

volumes:
  sqldata:
```

```bash
docker compose up -d
# Wait ~90–120 seconds for SQL Edge to initialise (ARM64 QEMU warm-up)
```

---

## 3. Key Vault Access (Local Dev)

PoTraffic uses `DefaultAzureCredential` → `AzureCliCredential` chain for local Key Vault reads.

```bash
az login                          # one-time per session
az account set --subscription <SUBSCRIPTION_ID>
```

Ensure your Azure identity has been granted **Key Vault Secrets User** role on the `PoShared` Key Vault:

```bash
az role assignment create \
  --role "Key Vault Secrets User" \
  --assignee "<your-azure-ad-object-id>" \
  --scope "/subscriptions/<SUBSCRIPTION_ID>/resourceGroups/<RG>/providers/Microsoft.KeyVault/vaults/PoShared"
```

> **ARM64 tip**: Set `ExcludeManagedIdentityCredential=true` in `appsettings.Development.json` to skip the 1-second Managed Identity probe and speed up startup by ~3–5 seconds.

---

## 4. User Secrets / appsettings.Development.json

The connection string is expected in user secrets or a local override:

```bash
dotnet user-secrets set "ConnectionStrings:Default" \
  "Server=localhost,1433;Database=PoTraffic;User=sa;Password=Dev!Passw0rd;TrustServerCertificate=true" \
  --project src/PoTraffic.Api
```

Optional overrides in `appsettings.Development.json`:

```json
{
  "AzureKeyVault": {
    "VaultUri": "https://PoShared.vault.azure.net/"
  },
  "AzureCredential": {
    "ExcludeManagedIdentityCredential": true
  },
  "Hangfire": {
    "DashboardPath": "/hangfire"
  },
  "Serilog": {
    "MinimumLevel": {
      "Default": "Debug",
      "Override": {
        "Microsoft": "Warning",
        "Hangfire": "Information"
      }
    }
  }
}
```

---

## 5. Apply Database Migrations

```bash
dotnet ef database update \
  --project src/PoTraffic.Api \
  --startup-project src/PoTraffic.Api
```

This creates all application tables and seeds `SystemConfiguration` rows:
- `cost.perpoll.googlemaps`
- `cost.perpoll.tomtom`
- `quota.daily.default` (value: `10`)
- `quota.reset.utc` (value: `00:00`)

Hangfire tables are created automatically on first API startup in the `hangfire` schema.

---

## 6. Run the API

```bash
dotnet run --project src/PoTraffic.Api
# Or with hot reload:
dotnet watch --project src/PoTraffic.Api
```

Key URLs (default ports):

| URL | Purpose |
|---|---|
| `https://localhost:7080` | API base |
| `https://localhost:7080/hangfire` | Hangfire dashboard |
| `https://localhost:7080/swagger` | OpenAPI (Development only) |
| `https://localhost:7080/health` | Health check endpoint |

---

## 7. Run the Blazor WASM Client

In a second terminal:

```bash
dotnet run --project src/PoTraffic.Client
# Default: https://localhost:7081
```

> In production the client static files are served from `PoTraffic.Api/wwwroot` via the WASM publish pipeline. Locally, the two projects run on separate ports for hot reload.

---

## 8. Run Tests

### Unit Tests

```bash
dotnet test tests/PoTraffic.UnitTests
```

### Integration Tests (Testcontainers)

Requires Docker running. SQL Edge container starts automatically (~120s first run, ~30s on subsequent runs with warm layers).

```bash
dotnet test tests/PoTraffic.IntegrationTests
```

> Testcontainers 3.x is required (not 4.x — open issues with WebApplicationFactory in 4.x as of Feb 2026). Set `ContainerStartupTimeout` ≥120 seconds in `BaseIntegrationTest`.

### E2E Tests (Playwright .NET / C#)

E2E tests require a running API with `ASPNETCORE_ENVIRONMENT=Testing` to activate the `/e2e/dev-login` and `/e2e/seed` endpoints.

```bash
# Terminal 1 — Run API in Testing mode
ASPNETCORE_ENVIRONMENT=Testing dotnet run --project src/PoTraffic.Api

# Terminal 2 — Install browsers (first run only)
pwsh tests/PoTraffic.E2ETests/playwright.ps1 install chromium

# Terminal 3 — Run E2E suite
dotnet test tests/PoTraffic.E2ETests
```

> The `/e2e/dev-login` and `/e2e/seed` endpoints return `404` in all other environments (not registered in routing table). Verify this with: `curl https://localhost:7080/e2e/dev-login` — must return `404` when `ASPNETCORE_ENVIRONMENT=Development`.

---

## 9. Integration Scenarios

### Scenario A — First-time User Flow

1. Call `POST /api/auth/register` with email + password + locale (`Europe/London`)
2. Call `POST /api/auth/login` → receive JWT bearer token
3. Call `POST /api/routes` to create a commute route (provider: GoogleMaps)
4. Call `POST /api/routes/{id}/windows` to set a monitoring window (08:00–09:00, Mon–Fri)
5. Call `POST /api/routes/{id}/windows/{windowId}/start` → returns `quotaRemaining`
6. The Hangfire polling chain begins. Observe poll records at `GET /api/routes/{id}/poll-history`
7. After ≥3 same-weekday sessions, `GET /api/routes/{id}/baseline` returns mean + σ values

### Scenario B — Reroute Detection

1. Seed route with poll records where `distance_metres` increases ≥15% across 2 consecutive polls
2. Call `GET /api/routes/{id}/poll-history` — verify `isRerouted: true` on the flagged record
3. Verify the Blazor chart highlights the rerouted segment

### Scenario C — Daily Quota Exhaustion

1. Set `quota.daily.default` to `2` in `SystemConfiguration` (admin endpoint)
2. Start 2 monitoring sessions → `quotaRemaining: 0`
3. Attempt to start a third session → expect `429 Too Many Requests`
4. Advance clock past midnight UTC (using `POST /e2e/seed` with `"scenario": "advance-clock"` in Testing env) → quota resets

### Scenario D — GDPR Hard Delete

1. Create user, add routes, generate poll records
2. Call `DELETE /api/account` → expect `204`
3. Verify all cascade-deleted rows in SQL: `Users`, `Routes`, `MonitoringWindows`, `MontitoringSessions`, `PollRecords` for that user

### Scenario E — Admin Dashboard

1. Login as a seeded admin user (`POST /e2e/dev-login` with `"role": "admin"` in Testing env)
2. Call `GET /api/admin/users` → verify all users with usage metrics
3. Call `GET /api/admin/poll-cost-summary` → verify per-provider cost breakdown
4. Update `cost.perpoll.googlemaps` via `PUT /api/admin/system-configuration/cost.perpoll.googlemaps`

---

## 10. Hangfire Dashboard

Navigate to `https://localhost:7080/hangfire`. You will see:

- **Enqueued** — any pending poll jobs
- **Scheduled** — next poll jobs (self-scheduled 5-minute delay chains)
- **Succeeded / Failed** — completed jobs with full exception detail on failure
- **Recurring** — empty (recursive pattern does not use `RecurringJob.AddOrUpdate`)

To trigger an immediate poll outside the schedule:

```bash
curl -X POST https://localhost:7080/api/routes/{id}/check-now \
  -H "Authorization: Bearer <token>"
```

---

## 11. Logging

Serilog is wired as the sole MEL backend. In Development, logs are written to Console (with structured output) and optionally to a file sink. All code uses `ILogger<T>` — no direct Serilog API calls outside `Program.cs`.

To see WASM client logs forwarded to the server:

1. Open the Blazor app in the browser
2. The custom `ILoggerProvider` batches and POSTs logs to `POST /api/client-logs`
3. Logs appear in the API console with `[WASM]` source tag and `CorrelationId` / `SessionId` properties

---

## 12. Troubleshooting

| Symptom | Likely Cause | Fix |
|---|---|---|
| API startup hangs 3–5s | Managed Identity probe | Add `"ExcludeManagedIdentityCredential": true` in `appsettings.Development.json` |
| SQL Edge container not ready | ARM64 QEMU startup time | Wait 90–120s; check `docker logs potraffic-sqlserver-1` |
| Hangfire jobs not executing | Hangfire server not started | Ensure `app.UseHangfireServer()` is called in `Program.cs` |
| Testcontainers timeout | Container startup > 30s default | Set `ContainerStartupTimeout = TimeSpan.FromSeconds(120)` |
| `/e2e/dev-login` returns 404 in Dev | Expected behaviour | Only registered in `ASPNETCORE_ENVIRONMENT=Testing` |
| Chart not updating | Blazor reference not replaced | Replace the collection reference (new `List<T>`); do not mutate in-place |
| STDDEV query returns NULL | Fewer than 2 poll records for slot | Show "building baseline" UI state (FR-012: min 3 sessions) |
