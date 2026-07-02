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
| Back-end | ASP.NET Core 10 Minimal API + in-house validation-first Dispatcher (`Infrastructure/Dispatch`) |
| Persistence | Table Storage (Azurite locally, Azure in prod) |
| Background Jobs | Table Storage-backed scheduler (recursive polling + nightly pruning) |
| Auth | BFF cookie sessions (HttpOnly, SameSite=Strict); Microsoft OAuth (Dev/Prod **required**) + Testing-only bypasses |
| Logging | Serilog (Console, File, App Insights) + structured fields |
| Caching | `HybridCache` (in-proc L1; optional distributed L2) — `Infrastructure/Caching` |
| HTTP Resilience | `Microsoft.Extensions.Http.Resilience` (per-client named pipelines) — `Infrastructure/Resilience` |
| Observability | OpenTelemetry → Azure Monitor / App Insights in `rg-poshared` |
| Testing | xUnit + NSubstitute (unit), Testcontainers (integration), pure-HTTP (E2EAPI), Playwright (E2EUI) |

## 3. Architecture (Vertical Slice)

```
PoTraffic.Api             // Single host — Features/<Feature>/ slices (endpoints,
                          // commands, handlers, entities) + Infrastructure/ cross-cutting
PoTraffic.Client          // Blazor WASM — hosted by Api, no CORS
PoTraffic.Shared          // DTOs/Enums/Constants shared between Api ↔ Client
tests/
  PoTraffic.Tests         // Unit + Integration (folder split via <Compile/>)
  PoTraffic.Tests.E2E     // Playwright (Ui/) + HTTP (Api/) end-to-end scenarios
```

**Rules:**
- No Onion/Clean layer projects — slices live in `PoTraffic.Api/Features/<FeatureName>/`
  (endpoints, commands/queries, handlers, validators, entities together).
- Cross-cutting only under `PoTraffic.Api/Infrastructure/` (Dispatch, Storage,
  Scheduling, Security, Observability, Testing).
- Requests dispatch through `ISender` (`Infrastructure/Dispatch/Dispatcher.cs`):
  FluentValidation runs before every handler; `ValidationException` → 422.
- **No** `Services/`, `Repositories/`, `DTOs/` folders at the project root.

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
  vs managed identity against `AzureTable:AccountName`. The Azurite container is
  started by `docker-compose.yml`.
- **Durable persistence:** `TableStorageContext` hydrates the working set from
  Table Storage at startup (`HydrateAsync`) and `SaveChangesAsync` writes the
  delta (JSON snapshot diff + queued deletes) — one table per entity, poll
  records partitioned by `RouteId`. Prod fails fast if storage is unreachable;
  Dev degrades to memory-only (surfaced by the `storage` health check).

## 6. Auth (Rules 6 + 13)

- **Environment matrix (Rule 4.4):**
  - **Testing** — GUEST bypass (automated tests skip interactive auth; `/e2e/*` available).
  - **Development** — Microsoft OAuth **and** a "Continue as Guest" bypass button.
  - **Production** — Microsoft OAuth **only**; `MapGuestEndpoints` throws if a
    Production host ever registers it.
- `/api/auth/providers` returns `guestEnabled` so the login page renders the right view.
- **GUEST format:** `GUEST` + 8 random digits (e.g. `GUEST12345678`).
  Display in the nav bar as `GUEST12345678 LOGGED IN`.
- **Persistence:** BFF pattern — the session is an HttpOnly `SameSite=Strict`
  cookie (`.PoTraffic.Auth`) issued by `CookieSignIn`; the WASM client never
  sees a token. Client auth state comes from `GET /api/auth/me`
  (`CookieAuthenticationStateProvider`). There is no password or JWT stack.

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
- `/health` — JSON health response with `keyvault`, `trafficProvider`,
  `storage` (durability), and `scheduler` (tick liveness) entries.

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
- ❌ Do not bypass `app.MapOpenApi()` from `Microsoft.AspNetCore.OpenApi` —
  Scalar is wired in `Program.cs` against the registered OpenAPI document.
  The package reference is required (see `PoTraffic.Api.csproj`).
- ❌ Do not add a `Services/` project — the API project is the only host.
- ❌ Do not add GUEST or dev-admin login to Development or Production — Microsoft
  OAuth is the only normal sign-in path.

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

The [maddhruv/absolute](https://github.com/maddhruv/absolute) workflow pack
(11 skills: `absolute-init/-work/-spec/-ui/-simplify/-docs/-upgrade/-audit/
-prune/-debt/-deflake`) is installed project-level under `.claude/skills/`
(tracked, with `skills-lock.json`). Reinstall/update via
`npx skills add maddhruv/absolute --skill '*' --agent claude-code -y`.

## 12. Key Files

- `global.json` — pins .NET 10.
- `Directory.Build.props` — strict warnings, MinVer, C# 14.
- `Directory.Packages.props` — all NuGet versions (CPM).
- `PoTraffic.slnx` — solution layout.
- `azure.yaml` — azd project mapping.
- `docker-compose.yml` — Azurite for local Table Storage.
- `src/PoTraffic.Api/Program.cs` — composition root.
- `src/PoTraffic.Api/Infrastructure/Resilience/ResiliencePipelineExtensions.cs` — named HTTP resilience pipelines (`traffic`, `external-auth`).
- `src/PoTraffic.Api/Infrastructure/Caching/HybridCacheExtensions.cs` — HybridCache registration.
- `docs/ProductSpec.md` — what we're building.
- `docs/Architecture.mmd` — C4-style architecture diagram.
- `docs/ApiContract.md` — HTTP surface area.

## 13. Audit Trail

Verified properties of the codebase as of 2026-07-02:

| Concern | Status | Notes |
|---|---|---|
| `<Nullable>enable</Nullable>` | ✅ Enforced | `Directory.Build.props` |
| `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` | ✅ Enforced | `Directory.Build.props` |
| `<EnableTrimAnalyzer>true</EnableTrimAnalyzer>` | ✅ Enforced in `PoTraffic.Shared`, `PoTraffic.Client` |
| CPM (no inline `Version` in `.csproj`) | ✅ Verified | `Directory.Packages.props` |
| Vertical Slice Architecture (no `Services/`, `Repositories/`, `DTOs/` at project root) | ✅ Verified |
| Slug-pattern `Po{Name}` | ✅ All assemblies/root namespaces |
| GUEST → `InvalidOperationException` in Production | ✅ Verified in `GuestAuthExtensions.MapGuestEndpoints` |
| Microsoft OAuth required in Dev/Prod | ✅ Verified in `ProductionMicrosoftAuthPolicy` |
| Cookie name `.PoTraffic.Auth`; `HttpOnly`, `SameSite=Strict` | ✅ Verified in `SecurityExtensions` |
| Testcontainers Azurite (no stale manual mocks) | ✅ Verified in `Integration/Infrastructure/AzuriteTestContainer.cs` |
| Named HTTP resilience pipelines | ✅ `ResiliencePipelineExtensions.TrafficPipeline`, `ExternalAuthPipeline` |
| HybridCache registered | ✅ `HybridCacheExtensions.AddPoTrafficHybridCache` |
| Key Vault pulled via Managed Identity | ✅ `DefaultAzureCredential` in `TableStorageExtensions` (Azure path) |
| GH Actions workflow count | 1 (`deploy.yml` — build + Azure App Service deploy) |
| Mobile-portrait nav layout | ✅ `app-shell` grid (Left=Brand / Center=Actions / Right=User) |
| "USING MOCK DATA" banner | ✅ `pt-mock-banner` rendered below navbar when `Features.UseMockProviders` |
| Scoped `.razor.css` (no inline `style=""`) | ✅ Top offenders migrated (Dashboard, CreateRoute, RouteCard, RouteAddressDisplay) |

---

*Last updated when the audit checklist was applied (see `SCRIPTS/README.md`).*
