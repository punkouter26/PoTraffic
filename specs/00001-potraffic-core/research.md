# Research: PoTraffic Technical Stack

**Sub-agent**: `sddp.Researcher`
**Date**: 2026-02-19
**Feature**: PoTraffic — Empirical Commute Volatility Engine
**Scope**: Implementation-stack technical gaps

> **Prior research**: Domain-level findings (traffic data patterns, UX, visualization, quota management, reroute detection, admin dashboards, departure-time optimization, data retention) are documented in [`research_domain.md`](research_domain.md). This file covers only the implementation-stack technical gaps not addressed there.

---

## 1. Hangfire Recursive Job Pattern for 5-Minute Polling

- **Decision**: Use the recursive self-scheduling pattern — each job schedules its successor as its last action via `BackgroundJob.Schedule` with a 5-minute delay, wrapped in a `finally` block to survive handler exceptions.
- **Rationale**: The `RecurringJob.AddOrUpdate` cron alternative fires at absolute wall-clock intervals, causing job pile-up if a single poll exceeds 5 minutes. Recursive scheduling enforces a minimum 5-minute gap between completions, eliminates pile-up by construction, and allows per-route cancellation by simply not rescheduling.
- **Alternatives**: `RecurringJob.AddOrUpdate` with `*/5 * * * *` cron — pile-up risk unless `DisableConcurrentExecution` is applied, which then serialises all routes per user and introduces cascading delays. Rejected.
- **Pitfalls**: Store the `BackgroundJob.Schedule`-returned `jobId` on the route entity and call `BackgroundJob.Delete(jobId)` on route deletion to prevent ghost chains. Emit an OTel gauge for "active polling chains per user" to detect silent chain deaths.

---

## 2. Hangfire + EF Core VSA Layout

- **Decision**: Hangfire job classes live in `PoTraffic.Api/Features/<FeatureName>/` as thin orchestrators that constructor-inject `IServiceScopeFactory`, create a DI scope, and dispatch a MediatR `Command`. The Hangfire `JobActivator` DI bridge lives in `PoTraffic.Api/Infrastructure/Hangfire/`.
- **Rationale**: SRP is preserved — the job class owns scheduling/retry concerns; the MediatR handler owns business logic. `IServiceScopeFactory` is mandatory because Hangfire jobs execute outside the HTTP request lifetime with no ambient DI scope; resolving `DbContext` from the root container throws at runtime.
- **Alternatives**: Implement `IRequestHandler<T>` directly on the job class (dual role) — violates SRP, untestable without Hangfire infrastructure. Inject `DbContext` directly into the job class — duplicates handler logic. Both rejected.
- **Pitfalls**: Register all job classes explicitly via `services.AddScoped<MyJobClass>()`. Never capture a scoped `DbContext` as a constructor-injected parameter on a job class.

---

## 3. Blazor WASM Log Forwarding to Serilog

- **Decision**: Implement a custom `ILoggerProvider`/`ILogger` in `PoTraffic.Client` that batches structured entries and POSTs them as a JSON array to `POST /api/client-logs`. The Minimal API endpoint enriches each entry with `CorrelationId` and `SessionId` from the payload and writes to the Serilog pipeline via `Log.ForContext(...)`. Correlation ID and Session ID are generated client-side (GUIDs, stored in `sessionStorage`).
- **Rationale**: Blazor WASM has no direct access to server-side sinks. The HTTP push model is simple, inherits auth middleware, and preserves structured log context without string interpolation. SignalR is disproportionate for log forwarding at a 5-minute cadence.
- **Alternatives**: Azure App Insights JavaScript SDK — bypasses Serilog, breaks unified client/server log correlation. `console.log` only — unstructured, unqueryable. Both rejected.
- **Pitfalls**: Cap batch size (≤50 entries) and rate-limit emission (max once per 10 seconds). Never use client-supplied strings as Serilog message templates (injection risk). Do not log route addresses or user location in client logs (GDPR data minimisation).

---

## 4. OpenTelemetry 50% Sampling on Hangfire Jobs

- **Decision**: Implement a custom `ActivitySampler` that inspects `ActivitySource.Name`; for sources matching `"Hangfire*"`, delegate to `TraceIdRatioBased(0.5)`; for all others, return `RecordAndSample`. Register via `tracerProviderBuilder.SetSampler(new CompositeRoutingSampler())`.
- **Rationale**: The built-in `TraceIdRatioBased` sampler applies a single rate to all activities. Per-source rate control requires a custom composite sampler. The `Hangfire.Processing` ActivitySource is the discriminator. 50% for high-frequency pings reduces App Insights ingestion cost without degrading observability of user-facing API endpoints where 100% is needed.
- **Alternatives**: Global `TraceIdRatioBased(0.5)` — halves observability on user API calls, making P99 latency analysis unreliable. `FilteringProcessor` at exporter level — filters after sampling, doesn't reduce trace creation overhead. Both rejected.
- **Pitfalls**: `Hangfire.Processing` source name is not a public API contract — pin it to a constant, add a canary test. Ensure Hangfire activation activities are trace roots (no inbound HTTP parent) to prevent the parent's `RecordAndSample` from overriding the sampler's decision.

---

## 5. EF Core + Azure SQL STDDEV for ±1σ Baseline

- **Decision**: Use `DbContext.Database.SqlQueryRaw<BaselineSlotDto>` with a single Azure SQL query using `AVG(duration)` and `STDEV(duration)` (sample standard deviation) grouped by `(day_of_week, time_slot_bucket)`. Map results to a read-only projection DTO with `double?` for the σ column.
- **Rationale**: EF Core's LINQ-to-SQL translator (including EF Core 10) does not natively support `STDDEV_POP`/`STDDEV_SAMP` via expression trees. An in-process approximation would require materialising up to 21,600 rows per user — unacceptable. `SqlQueryRaw<T>` executes the aggregation at the SQL engine with compile-time DTO typing.
- **Alternatives**: Compute σ in C# after `ToListAsync()` — O(N) memory allocation per user. Pre-aggregated baseline table updated nightly — reduces read latency but adds write complexity and staleness risk; deferred post-MVP.
- **Pitfalls**: `STDEV()` returns `NULL` for groups with fewer than 2 rows — treat `NULL` σ as "insufficient data" and show the "building baseline" UI state. Always parameterise `SqlQueryRaw` inputs via `SqlParameter` to prevent SQL injection.

---

## 6. Testcontainers + Azure SQL Edge ARM64

- **Decision**: Use `testcontainers-dotnet` 3.x with `MsSqlBuilder` targeting `mcr.microsoft.com/azure-sql-edge`. Set `ContainerStartupTimeout` ≥120 seconds. On ARM64 developer machines, prefer an externally-running shared SQL Edge container for hot-reload test loops, pointing integration tests at it via environment variable.
- **Rationale**: `testcontainers-dotnet` 3.x is the established path for SQL Edge integration tests, confirmed compatible with .NET 10 and `WebApplicationFactory<T>`. SQL Edge on ARM64 runs under QEMU emulation (functionally correct, ~90–120s startup). Version 4.x has open lifecycle issues with `WebApplicationFactory` as of February 2026.
- **Alternatives**: SQL Server 2022 Developer container — not ARM64-native, 3× larger image. LocalDB — Windows-only, incompatible with Linux CI runners. Both rejected.
- **Pitfalls**: Azure SQL Edge lacks `APPROX_COUNT_DISTINCT` and some spatial index types — verify all EF Core migrations against SQL Edge's feature matrix. Use a strong hardcoded test password (uppercase + lowercase + digit + special, ≥8 chars) or the container starts but the MSSQL service crashes silently. Dispose containers in `IAsyncLifetime.DisposeAsync` to avoid synchronous disposal deadlocks in xUnit.

---

## 7. Radzen Chart Real-Time Updates in Blazor WASM

- **Decision**: Use `PeriodicTimer` inside the monitoring dashboard component's `OnInitializedAsync`. On each tick, fetch new poll data from the API, replace the chart's bound `IEnumerable<PollPoint>` reference with a new `List<T>` instance, then call `await InvokeAsync(StateHasChanged)`.
- **Rationale**: `RadzenChart` triggers a re-render when its bound `Data` parameter reference changes. `PeriodicTimer` integrates naturally with `await`, avoids overlapping callbacks (if the API call exceeds 5 minutes, the next tick is skipped), and prevents the double-fired timer issue of `System.Threading.Timer`.`InvokeAsync(StateHasChanged)` is mandatory because timer callbacks run on a ThreadPool thread, not on Blazor's synchronisation context.
- **Alternatives**: SignalR server push — architecturally correct for true real-time but disproportionate for a 5-minute interval; adds WebSocket infrastructure for no UX benefit at this cadence. Rejected.
- **Pitfalls**: Mutating the existing list in-place without replacing the reference will not trigger Radzen's re-render — always create a new `List<T>`. Dispose the `PeriodicTimer` in `IAsyncDisposable.DisposeAsync` to prevent ghost timers after navigation. Never call `StateHasChanged` outside `InvokeAsync` from a threadpool thread.

---

## 8. Azure Managed Identity + Key Vault in Local Dev

- **Decision**: Use `DefaultAzureCredential` unconditionally (no environment branching) in `Program.cs` via `builder.Configuration.AddAzureKeyVault(vaultUri, new DefaultAzureCredential())`. Developers run `az login` once; `DefaultAzureCredential` resolves via `AzureCliCredential` locally and via Managed Identity in production.
- **Rationale**: `DefaultAzureCredential` implements Chain of Responsibility across credential providers — first successful resolution wins. This eliminates environment-specific credential code paths and configuration drift. `Azure.Identity` 1.12+ is required for full .NET 10 compatibility.
- **Alternatives**: `appsettings.Development.json` with gitignored secrets — breaks CI/CD pipelines without `az login`, creates secret-sprawl risk. `VisualStudioCredential` — IDE-specific, not portable. Both rejected.
- **Pitfalls**: In local dev, `DefaultAzureCredential` probes Managed Identity first (1s timeout) before reaching `AzureCliCredential`, adding ~3–5 seconds to startup. Mitigate by setting `ExcludeManagedIdentityCredential = true` in `DefaultAzureCredentialOptions` for the `Development` environment. Document the required Key Vault role assignment (`Key Vault Secrets User`) in the project README.

---

## 9. /dev-login + /seed E2E Endpoint Security

- **Decision**: Register test-only endpoints in a `TestingEndpoints` static class via a `MapTestingEndpoints(this IEndpointRouteBuilder app)` extension method, called in `Program.cs` exclusively inside `if (app.Environment.IsEnvironment("Testing"))`. The `"Testing"` environment is activated only by setting `ASPNETCORE_ENVIRONMENT=Testing` in the Playwright test runner — this value is never present in App Service configuration.
- **Rationale**: Conditional endpoint registration means `/dev-login` and `/seed` are not in the routing table in non-Testing environments — attempts return `404`, not `401`. This is stronger than auth-guarding (which still routes). `"Testing"` is deliberately distinct from `"Development"` so local dev mode does not accidentally expose the endpoints.
- **Alternatives**: `#if DEBUG` compile guards — `DEBUG` is a build configuration, not an environment; fragile and confusing. Separate `TestStartup` with `WebApplicationFactory<TestStartup>` — architecturally cleaner but complicates CI pipeline entry point; deferred post-MVP hardening.
- **Pitfalls**: Add a dedicated integration test asserting `GET /dev-login` returns `404` with `ASPNETCORE_ENVIRONMENT=Production`. Use a unique, unguessable path prefix (e.g., `/e2e-{token}/seed`) as defence-in-depth against misconfigured staging environments.

---

## 10. VSA Source Layout: Blazor WASM + .NET API Monorepo

- **Decision**: Three-project solution: `PoTraffic.Api` (ASP.NET Core Minimal API, EF Core, Hangfire, MediatR), `PoTraffic.Client` (Blazor WASM), `PoTraffic.Shared` (`net10.0` class library: cross-boundary DTOs, enums, value objects). Feature slices are organised independently under `PoTraffic.Api/Features/<Name>/` and `PoTraffic.Client/Features/<Name>/`. Shared infrastructure lives in `PoTraffic.Api/Infrastructure/`. `PoTraffic.Client` is referenced in `PoTraffic.Api` with `<ReferenceOutputAssembly>false</ReferenceOutputAssembly>` to trigger the WASM publish pipeline without type leakage.
- **Rationale**: `PoTraffic.Shared` eliminates DTO duplication across the HTTP boundary. EF Core entities, MediatR handlers, FluentValidation validators, and Hangfire job classes remain exclusively server-side. The `ReferenceOutputAssembly=false` technique is supported by .NET 10 SDK 10.0.100+.
- **Alternatives**: Two-project layout with duplicated DTOs — DTO drift over time; rejected. Four-project layout adding `PoTraffic.Domain` — over-layered for a single-developer VSA project at this scale; reserved as a future refactor target. Single-project hosted Blazor — conflates server and client concerns, hurts test isolation; rejected.
- **Pitfalls**: `PoTraffic.Shared` must declare zero dependencies on EF Core, ASP.NET Core, MediatR, or Hangfire — any such reference bloats the WASM payload and may cause `PlatformNotSupportedException`. Do not place FluentValidation validators in `PoTraffic.Shared`; keep them server-side and share only constant rule values (max lengths, regex patterns) as constants.

---

## Summary

| Topic | Decision | Rationale |
|---|---|---|
| Polling scheduler | Hangfire recursive self-scheduling | Prevents pile-up; per-route cancellation; built-in persistence and dashboard |
| Job structure | Thin Hangfire dispatcher → MediatR command | SRP; testable; VSA-compliant |
| Client logging | Custom Blazor ILoggerProvider → `POST /api/client-logs` → Serilog | Simple; inherits auth; structured correlation |
| OTel sampling | Custom composite sampler (50% Hangfire, 100% rest) | Per-source rate control; preserves API call observability |
| STDDEV aggregation | `SqlQueryRaw<BaselineSlotDto>` with `STDEV()` | EF Core lacks native STDDEV; engine-level aggregation optimal |
| Test containers | testcontainers-dotnet 3.x + MsSqlBuilder (SQL Edge ARM64) | Confirmed .NET 10 compatible; ARM64 via QEMU |
| Chart live updates | `PeriodicTimer` + reference replacement + `InvokeAsync(StateHasChanged)` | Triggers Radzen re-render; avoids SignalR overhead |
| Local secrets | `DefaultAzureCredential` + `az login` | No environment branching; portable; production-parity |
| Test endpoints | `if (Environment == "Testing")` conditional registration | Routes not registered in production — 404 not 401 |
| Solution layout | 3 projects: Api / Client / Shared | Eliminates DTO duplication; prevents server-type leakage into WASM |

## Sources Index

| Topic | Source |
|---|---|
| Hangfire recursive pattern | Hangfire.io documentation, community forum discussions |
| OTel composite sampler | OpenTelemetry .NET SDK docs (ActivitySampler) |
| Testcontainers .NET | testcontainers-dotnet GitHub + .NET 10 compat notes |
| Radzen real-time chart | Radzen Blazor documentation (RadzenChart, Data binding) |
| DefaultAzureCredential | Azure.Identity .NET SDK documentation |
| Blazor WASM publish to wwwroot | Microsoft Learn — Blazor hosting and deployment |
