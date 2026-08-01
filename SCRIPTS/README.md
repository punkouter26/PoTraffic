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
| `post-deploy-smoke.ps1` | CI/CD rule #9 — runs three browser-style smoke checks against a freshly-deployed App Service instance: `/health/json` (dependency status), `/health/ready` (hydration complete), `GET /` (render-tree / Blazor bundle hash), `/diag/keyvault` (Key Vault + Managed Identity wiring, optional). Exits non-zero if any check fails. |
| `triage-50030.ps1` | Forensic triage for `HTTP Error 500.30 - ASP.NET Core app failed to start`. Captures App Service instance state, downloads + greps the application-log filesystem files for `500.30`, `HostingStartupException`, `AuthorizationPermissionMismatch`, `HydrationFailed`, lists storage RBAC for the target account, live-probes `/health/json`, `/health/ready`, `/health`, `/`, and tails logs. **Read-only — never mutates the environment.** Use this when a deploy goes red. |
| `arg-governance.ps1` | Lints inline `style="…"` usages in `.razor` files. |

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
