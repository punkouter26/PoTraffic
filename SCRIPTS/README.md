# SCRIPTS

PowerShell / Python utility scripts for local development and CI. Run all scripts from the **repository root** unless noted otherwise.

---

## Scripts

| File | Purpose |
|------|---------|
| `setup.ps1` | First-time setup: installs missing tools via `winget` (.NET SDK, Docker Desktop, Azure CLI), checks `az login`, verifies Key Vault access, and starts Azurite. (Rule 9) |
| `start-dev.ps1` | Starts all local dependencies (Azurite via Docker) and then launches the PoTraffic API + Blazor client. Kills any existing `dotnet` processes on port 5000/5001 first. |
| `stop-dev.ps1` | Stops and removes the local Docker containers (Azurite). |
| `run-tests.ps1` | Runs Unit → Integration → E2E tests in order. Integration tests use in-memory persistence (no external dependencies). E2E tests require the API to be running on port 5000 (`Testing` profile). |

---

## Prerequisites

- .NET 10 SDK (`global.json` pins the version)
- Docker Desktop (for Azurite storage emulator in `docker-compose.yml`)
- PowerShell 7+

---

## Quick start (first checkout)

```powershell
# 1. Start Azurite storage emulator
docker compose up -d

# 2. Run the app (kills any stale dotnet processes first)
./SCRIPTS/start-dev.ps1
```
