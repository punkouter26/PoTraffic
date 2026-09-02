# SCRIPTS

PowerShell / Python utility scripts for local development and CI. Run all scripts from the **repository root** unless noted otherwise.

---

## Scripts

| File | Purpose |
|------|---------|
| `setup.ps1` | First-time setup: installs missing tools via `winget` (.NET SDK, Docker Desktop, Azure CLI), checks `az login`, verifies Key Vault access, and starts Azurite. (Rule 9) |
| `start-dev.ps1` | Starts all local dependencies (Azurite via Docker) and then launches the PoTraffic API + Blazor client. Kills any existing `dotnet` processes on port 5000/5001 first. |
| `stop-dev.ps1` | Stops and removes the local Docker containers (Azurite). |
| `run-tests.ps1` | Runs all four tiers and writes `TestResults/test-report.html`. Integration owns an Azurite container via Testcontainers (needs Docker); the E2E tiers get a `Testing` host started and stopped for them on `http://localhost:5150`. |
| `post-deploy-smoke.ps1` | Run automatically by the deploy workflow after every push to `master`, and runnable by hand against any instance. Browser-style smoke checks against a freshly-deployed App Service instance: `/health/json` (dependency status), `/health/ready` (hydration complete), `GET /` (render-tree / Blazor bundle hash), `/diag/keyvault` (Key Vault + Managed Identity wiring, optional). Exits non-zero if any check fails. |

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
