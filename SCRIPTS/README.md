# SCRIPTS

PowerShell / Python utility scripts for local development and CI. Run all scripts from the **repository root** unless noted otherwise.

---

## Scripts

| File | Purpose |
|------|---------|
| `start-dev.ps1` | Starts all local dependencies (Azurite via Docker) and then launches the PoTraffic API + Blazor client. Kills any existing `dotnet` processes on port 5000/5001 first. |
| `stop-dev.ps1` | Stops and removes the local Docker containers (Azurite). |
| `reset-db.ps1` | Drops and re-creates the local SQL Server development database, then applies all EF Core migrations. **Destructive — dev only.** |
| `seed-data.ps1` | Seeds the local database with sample routes and monitoring windows for manual testing. |
| `run-tests.ps1` | Runs Unit → Integration → E2E tests in order. Integration tests require Docker (Azurite + SQL via Testcontainers). E2E tests require the API to be running on port 5150 (`Testing` profile). |
| `publish-local.ps1` | Publishes the API project to `./publish/` in Release mode for local smoke-testing of the production build. |

---

## Prerequisites

- .NET 10 SDK (`global.json` pins the version)
- Docker Desktop (for Azurite in `docker-compose.yml` and Testcontainers)
- PowerShell 7+

---

## Quick start (first checkout)

```powershell
# 1. Start Azurite storage emulator
docker compose up -d

# 2. Run the app (kills any stale dotnet processes first)
./SCRIPTS/start-dev.ps1
```
