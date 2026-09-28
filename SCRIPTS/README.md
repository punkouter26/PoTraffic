# SCRIPTS

PowerShell utility scripts for local development and CI. Run all scripts from the **repository root** unless noted otherwise.

---

## Scripts

| File | Purpose |
|------|---------|
| `setup.ps1` | First-time setup: checks for the .NET SDK, Docker Desktop and Azure CLI (printing `winget` install hints for anything missing), checks `az login` and Key Vault access, then restores and builds the solution. |
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
# Starts Azurite (docker compose) and runs the app, killing any stale dotnet processes first
./SCRIPTS/start-dev.ps1
```
