# AGENTS.md — PoTraffic Agent Entry Point

> **This is the first file a coding agent should read.** It contains the context
> needed to work effectively on the PoTraffic codebase without re-discovering
> conventions. If something here is wrong or stale, fix this file first.

---

## 1. What is PoTraffic?

**PoTraffic** is a Blazor WebAssembly + ASP.NET Core 10 application that
empirically measures commute route volatility using Google Maps / TomTom APIs.
It records travel-time samples on a schedule, computes a baseline (mean ± σ)
per route / 5-minute time slot, and flags anomalies (congestion, reroutes) in
real time.

- **Solution name:** PoTraffic (mandatory `Po*` prefix).
- **Assembly / namespace prefix:** `PoTraffic.*` everywhere.
- **Resource group / Azure prefix:** `po*` (e.g. `rg-potraffic`, `app-potraffic`).

## 2. Stack Locked

| Layer | Technology |
|---|---|
| Front-end | Blazor WebAssembly (.NET 10) + Radzen Blazor |
| Back-end | ASP.NET Core 10 Minimal API + MediatR (CQRS) |
| Persistence | Table Storage (Azurite locally, Azure in prod) |
| Background Jobs | Table Storage-backed scheduler (recursive polling + nightly pruning) |
| Auth | Microsoft OAuth (Prod **required**) + GUEST (Dev/Test only) |
| Logging | Serilog (Console, File, App Insights) + structured fields |
| Observability | OpenTelemetry → Azure Monitor / App Insights in `rg-poshared` |
| Testing | xUnit + NSubstitute (unit), Testcontainers (integration), Playwright (E2E) |

## 3. Architecture (Onion)

```
PoTraffic.Domain          // Pure entities + value objects — no deps
PoTraffic.Application     // Interfaces + DTOs + validators — depends on Domain
PoTraffic.Infrastructure  // Table Storage, Azure, JWT, external providers
PoTraffic.Api             // ASP.NET host — features/ + minimal API endpoints
PoTraffic.Client          // Blazor WASM — hosted by Api, no CORS
PoTraffic.Shared          // DTOs/Enums/Constants shared between Api ↔ Client
```

**Rules:**
- Domain must not reference EF Core, ASP.NET, Azure, etc.
- Application must not reference Infrastructure or Api.
- Vertical slices under `Features/<FeatureName>/` — **no** `Services/`, `Repositories/`, `DTOs/` folders at the project root.

## 4. Build / Run

```powershell
# Restore + build
dotnet restore
dotnet build

# Local dev (kills stale dotnet on 5000/5001, starts Azurite, runs API)
./SCRIPTS/start-dev.ps1

# Tests
./SCRIPTS/run-tests.ps1

# Stop local Azurite
./SCRIPTS/stop-dev.ps1
```

**Fixed ports:** API HTTP `5000`, HTTPS `5001`. Blazor WASM is hosted by the API
(no CORS).

## 5. Configuration

- `appsettings.json` — non-sensitive defaults only.
- `appsettings.Development.json` — local dev overrides.
- `appsettings.Testing.json` — E2E / integration test overrides.
- **Production secrets:** Azure Key Vault `kv-poshared` in `rg-poshared`. Prefix
  is `PoTraffic--` (mapped to `:` by `PrefixKeyVaultSecretManager`).
- Connection strings flip automatically: `UseDevelopmentStorage=true` (Azurite)
  vs the Azure connection string. The Azurite container is started by
  `docker-compose.yml`.

## 6. Auth (Rules 6 + 13)

- **Prod:** Microsoft OAuth is **required**. GUEST login is rejected at the API.
- **Dev / Testing:** Microsoft OAuth + GUEST both allowed. The `LoginPage.razor`
  shows the GUEST button only when `IWebAssemblyHostEnvironment.IsDevelopment()`.
- **GUEST format:** `GUEST` + 8 random digits (e.g. `GUEST12345678`).
  Display in the nav bar as `GUEST12345678 LOGGED IN`.
- **Persistence:** The JWT is stored in `localStorage` under key
  `potraffic_access_token` by `JwtAuthenticationStateProvider`.

## 7. Quality Bar

- **Zero dead code** — remove unused files, packages, commented blocks before
  merge. Rule 9.
- **No AOT** — disabled globally. Rule 2 ("smaller build, faster CI/CD").
- **All NuGet versions in `Directory.Packages.props`** — no version in
  individual `.csproj` files (Central Package Management). Rule 3.
- **Treat warnings as errors** — `Directory.Build.props`. Rule 3.
- **XML doc comments on GoF / SOLID patterns** — explain *why* a pattern was
  chosen. Rule 2.
- **C# 14 features welcome** — `record` types, collection expressions, primary
  constructors. Rule 2.
- **Test env (mocks) for Integration + E2E**, dev env (real APIs) for local
  manual runs. Rule 7.

## 8. Observability (Rule 8)

- Serilog is wired as the MEL backend.
- OpenTelemetry exports traces + metrics to App Insights when
  `ApplicationInsights:ConnectionString` is set.
- `LogContextEnrichmentMiddleware` pushes `UserId` and `Environment` into every
  Serilog log scope.
- `/diag` (HTML) — connection status + masked keys. Available in dev and prod
  as a hidden page (see `DiagEndpoints.cs`).
- `/health` — JSON health response. Used by uptime pings.

## 9. Conventional Commands

```powershell
# Add a NuGet package (always via CPM)
# 1. Add <PackageVersion Include="X" Version="Y" /> to Directory.Packages.props
# 2. Add <PackageReference Include="X" /> (no Version) to the .csproj

# New EF Core migration
dotnet ef migrations add MyMigration --project src/PoTraffic.Api --startup-project src/PoTraffic.Api

# Apply migrations
dotnet ef database update --project src/PoTraffic.Api

# Build a specific project
dotnet build src/PoTraffic.Client

# Watch the API
dotnet watch --project src/PoTraffic.Api --launch-profile https
```

## 10. What NOT to do

- ❌ Do not introduce `Services/`, `Repositories/`, or `DTOs/` folders at the
  project root — features are vertical slices.
- ❌ Do not add `Version="..."` to `<PackageReference>` — CPM owns versions.
- ❌ Do not commit secrets to `appsettings.json` — use Key Vault or
  `appsettings.Development.json` (gitignored by convention).
- ❌ Do not enable AOT / trimming — keep the build small and CI fast.
- ❌ Do not create `setup.ps1` outside `/SCRIPTS` — all tools live there.
- ❌ Do not reference `Microsoft.AspNetCore.OpenApi` directly — Scalar is wired
  in `Program.cs`.
- ❌ Do not add a `Services/` project — the API project is the only host.
- ❌ Do not skip the GUEST button hide in Prod — it is enforced at both the API
  (`MapAnonEndpoints`) and the client (`IWebAssemblyHostEnvironment.IsDevelopment`).

## 11. Skills (Rule 12)

The following project-specific skills are expected in the agent's global config.
Also install the Addy Osmani `agent-skills` pack from
`https://github.com/addyosmani/agent-skills`; in Codex these map to the
general engineering skills such as `using-agent-skills`,
`incremental-implementation`, `test-driven-development`, `code-review-and-quality`,
`security-and-hardening`, and `shipping-and-launch`.

| Phase | Skill |
|---|---|
| 1 — Understand | `acquire-codebase-knowledge` |
| 2 — Design | `architecture-blueprint-generator`, `folder-structure-blueprint-generator` |
| 3 — Build | `dotnet-best-practices`, `dotnet-design-pattern-review`, `autoresearch` |
| 6 — Harden | `security-review` |
| 7 — Observability | `appinsights-instrumentation` |
| 8 — Deploy | `azure-deployment-preflight` |
| 9 — Operate | `azure-resource-health-diagnose` |
| 10 — Document | `create-readme`, `repo-story-time` |

If a skill is missing in the agent environment, prefer the patterns documented
in `docs/` instead.

## 12. Key Files

- `global.json` — pins .NET 10.
- `Directory.Build.props` — strict warnings, MinVer, C# 14.
- `Directory.Packages.props` — all NuGet versions (CPM).
- `PoTraffic.slnx` — solution layout.
- `azure.yaml` — azd project mapping.
- `docker-compose.yml` — Azurite for local Table Storage.
- `src/PoTraffic.Api/Program.cs` — composition root.
- `docs/ProductSpec.md` — what we're building.
- `docs/Architecture.mmd` — C4-style architecture diagram.
- `docs/ApiContract.md` — HTTP surface area.

---

*Last updated when the audit checklist was applied (see `SCRIPTS/README.md`).*
