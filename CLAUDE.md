# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

Working agreements — branching, pushing — are in [AGENTS.md](AGENTS.md). The short version:
commit to `master`, do not create branches unless asked, and do not push unasked (a push to
`master` deploys to production).

## What this is

PoTraffic measures commute-route volatility. It records travel-time samples on a schedule
(Google Maps / TomTom), builds per-route/time-slot baselines, and flags congestion and
reroute anomalies. Blazor WebAssembly client + ASP.NET Core minimal API on .NET 10, hosted
together as one App Service app (the API serves the WASM client — CORS is intentionally
never configured).

## Commands

```powershell
./SCRIPTS/start-dev.ps1        # kills stale dotnet on 5000/5001, starts Azurite, runs API (https profile)
./SCRIPTS/stop-dev.ps1         # docker compose down (Azurite)
dotnet build                   # TreatWarningsAsErrors=true — a warning is a build break
```

| URL (dev) | |
|---|---|
| `https://localhost:5001` | API + Blazor client |
| `https://localhost:5001/health` | Blazor health *page* (the SPA route owns `/health`) |
| `https://localhost:5001/health/json` | machine-readable dependency status |
| `https://localhost:5001/health/ready` | hydration complete? |
| `https://localhost:5001/scalar/v1` | Scalar API reference (Development only) |
| `https://localhost:5001/diag` | hidden diagnostics page |

### Tests

Four tiers. `run-tests.ps1` owns Azurite (Testcontainers), starts/stops the `Testing` host
on `http://localhost:5150` for the E2E tiers, and writes `TestResults/test-report.html`.

```powershell
pwsh ./SCRIPTS/run-tests.ps1                       # all tiers
pwsh ./SCRIPTS/run-tests.ps1 -Tier unit,integration
pwsh ./SCRIPTS/run-tests.ps1 -Tier e2e-ui -SkipBuild
```

Individual suites / single tests:

```powershell
dotnet test tests/PoTraffic.UnitTests
dotnet test tests/PoTraffic.UnitTests --filter "FullyQualifiedName~CreateRouteValidator"
dotnet test tests/PoTraffic.IntegrationTests   # needs Docker
dotnet test tests/PoTraffic.E2ETests          # needs a Testing host on E2E_BASE_URL
```

`E2E_HEADED=0` forces headless Playwright (headed is the local default). `E2E_BASE_URL`
overrides the E2E target.

## Architecture

### Projects (three, not the six the README claims)

`src/PoTraffic.API` (host + all server logic), `src/PoTraffic.Client` (Blazor WASM),
`src/PoTraffic.Shared` (DTOs, enums, strongly-typed IDs — referenced by both). The README's
"Domain / Application / Infrastructure" layout and its MediatR mention are stale.

Tests are `PoTraffic.UnitTests`, `PoTraffic.IntegrationTests` and `PoTraffic.E2ETests`, each
named for the tier it holds. The E2E project splits `Api/` (live HTTP) from `Ui/` (Playwright),
and namespaces follow the folders.

### Vertical slices

Server features live under `src/PoTraffic.API/Features/<Name>/`: one `*Endpoints.cs` that
maps a `MapGroup`, plus one file per operation holding the command/query record, its
`AbstractValidator`, and its handler together. Entities live in the slice that owns them
(`Features/Routes/Route.cs`, `Features/Auth/User.cs`). Do not add root-level `Services/`,
`Repositories/`, or `DTOs/` folders. Client features mirror this under
`src/PoTraffic.Client/Features/<Name>/`.

On the client the split is: `Components/` holds only shared, generic pieces that know
nothing about the domain (`PtIcon`, `PageHeader`, `UndoBar`); anything that knows what a
route is belongs to its feature slice; `Pages/` holds the app-shell routes that belong to
no feature (`/`, `/not-found`, `/access-denied`, `/health`).

### Dispatch

`Infrastructure/Dispatch/Dispatcher.cs` is an in-house MediatR replacement: `ISender.Send`
resolves every `IValidator<TRequest>`, runs them first, throws `ValidationException` on
failure (mapped to 422 by `GlobalExceptionHandler`), then invokes the single
`IRequestHandler<TRequest,TResponse>`. Handlers are auto-registered by assembly scan, so a
new slice needs no DI wiring. Validation is therefore always pre-handler — never
re-validate inside a handler.

### Persistence — the part most likely to surprise you

`Infrastructure/Storage/TableStorageContext.cs` keeps the **entire working set in memory**,
hydrated once at startup from Azure Table Storage, and exposes `IQueryable<T>` so handlers
read with plain LINQ. `SaveChangesAsync` diffs JSON snapshots and writes only the delta
(with ETag optimistic concurrency) through `ITableStore`. Consequences:

- Every entity is one row: keys from the `Maps` registry in `TableStorageContext`, payload
  as JSON in a single `Data` column — the schema evolves with the C# types, no migrations.
- Adding an entity type = one entry in `Maps` plus a backing list field.
- Queries are in-process and cheap; writes are not — batch them into one `SaveChangesAsync`.
- The parameterless constructor gives a volatile memory-only context for unit tests.
- `PollRecord` partitions by route and `Alert` by user; everything else uses `"main"`.

### Auth

Backend-for-frontend: an HttpOnly `SameSite=Strict` cookie (`.PoTraffic.Auth`) is the only
session credential — the WASM client never sees a token, and `CookieAuthenticationStateProvider`
derives auth state from `GET /api/auth/me`. Key points in
`Infrastructure/Security/SecurityExtensions.cs`:

- **Deny by default**: the authorization `FallbackPolicy` requires an authenticated user, so
  any endpoint without explicit metadata is protected. Genuinely public surfaces (login flow,
  health, `/diag`, `/error`, SPA shell) must call `.AllowAnonymous()` explicitly.
- Feature endpoints use `.RequireAuthorization("ProductionMicrosoftAuth")` — Microsoft OAuth
  is required in Development/Staging/Production and GUEST sessions are rejected there; the
  policy is a no-op only in `Testing`.
- Outside Production a `SmartCookieOrFake` policy scheme forwards to `FakeAuthHandler` when
  the `X-Fake-User` header is present (`X-Fake-Roles` for roles). `FakeAuthHandler` and
  `MapGuestEndpoints` throw if wired into a Production host.
- `Testing` alone exposes `Infrastructure/Testing/TestingEndpoints.cs` (`/e2e/dev-login`,
  `/e2e/seed-admin`, `/e2e/seed-route`, `/e2e/execute-poll`).

### Scheduling

`BackgroundSchedulerService` ticks every second against Table Storage-backed job rows,
executing each due job in its own DI scope. Job types (`PollRouteJob`,
`PruneOldPollRecordsJob`) are resolved from DI by the type name stored on the job, so a new
job type must also be registered in `Program.cs`. On startup it requeues jobs a crashed
process left `Running` and re-arms monitored routes whose poll chain died — polling is a
self-perpetuating chain, not a fixed timer.

### Traffic providers

`ITrafficProvider` implementations are registered as **keyed** services under `RouteProvider`
enum values and reached through `ITrafficProviderFactory`. When `Features:UseMockProviders`
is true (or the environment is `Testing`), both keys resolve to `MockTrafficProvider`. Live
clients are wired to the named resilience pipeline in `ResiliencePipelineExtensions`.

### Program.cs ordering constraints

`builder.AddPoTrafficKeyVault()` must stay before service registration (services read secrets
eagerly at registration time). `UseForwardedHeaders` runs first so HTTPS redirection sees the
real scheme behind App Service. `UseBlazorFrameworkFiles` + `UseStaticFiles` run before auth
so the client can load before sign-in.

### Shared IDs

`PoTraffic.Shared/Ids` defines `UserId`, `RouteId`, etc. as `readonly record struct`s
implementing `IParsable` (minimal-API binding works with no extra code) and serializing as
bare GUID strings. Construct with `New()` / `From(guid)`; unwrap via `.Value` only at a real
boundary. Do not introduce raw `Guid` parameters for entity identity.

## Conventions

- Central package versions only — edit `Directory.Packages.props`, never a `<Version>` in a
  `.csproj`. Shared MSBuild properties live in `Directory.Build.props` and must not be
  duplicated per-project.
- No AOT, and do not mark `PoTraffic.Client` `IsTrimmable` (routable `@page` components are
  found by a runtime route scan the trimmer cannot see). Static web asset fingerprinting is
  deliberately off in the client csproj — the hosted pipeline serves physical file names.
- Logging is Serilog through `ILogger<T>` only; `LogContextEnrichmentMiddleware` adds UserId
  and Environment to every event. `PiiRedactor` exists for anything user-identifying.
- Integration test methods use `[SkipUnlessAzuriteAvailable]`, not `[Fact]`, so the suite
  skips rather than hard-fails without Docker. E2E tests use `[ApiSkipUnlessReady]` /
  `[SkipUnlessE2EReady]`.
- CI (`.github/workflows/deploy.yml`) builds and deploys to App Service on `master` only and
  runs **no** tests — the local `run-tests.ps1` report is the gate.
