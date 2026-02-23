# Tasks: PoTraffic — Empirical Commute Volatility Engine

**Branch**: `00001-potraffic-core`
**Input**: `specs/00001-potraffic-core/plan.md` + `spec.md`
**Total Tasks**: 126

> Analysis remediations applied 2026-02-19: F-001 F-002 F-003 F-004 F-005 F-007 F-008 F-009 F-010 F-011 F-012 F-013 (see `@sddp.analyze` report). F-002 deferred to console-log (no SMTP at MVP).

| Phase | Tasks |
|---|---|
| Phase 1 — Setup | T001–T012 |
| Phase 2 — Foundational | T013–T033 |
| Phase 3 — US1 Monitoring Engine | T034–T054, T122–T123 |
| Phase 4 — US2 Dashboard | T055–T065, T116–T117 |
| Phase 5 — US3 Route Management + Auth | T066–T082 |
| Phase 6 — US4 Data Maintenance | T083–T086 |
| Phase 7 — US5 Admin | T087–T097, T118–T121, T126 |
| Phase 8 — US5 Account + GDPR | T098–T108 |
| Phase 9 — Polish | T109–T115, T124–T125 |

---

## Phase 1 — Setup

> Solution scaffolding, project creation, NuGet packages, CI skeleton, Docker Compose, Serilog bootstrap, OTel config.

- [X] T001 Create `PoTraffic.sln` and scaffold three source projects: `src/PoTraffic.Api`, `src/PoTraffic.Client`, `src/PoTraffic.Shared`
- [X] T002 [P] Scaffold three test projects: `tests/PoTraffic.UnitTests`, `tests/PoTraffic.IntegrationTests`, `tests/PoTraffic.E2ETests`; add project references to solution
- [X] T003 [P] Add NuGet packages to `src/PoTraffic.Api`: `MediatR`, `FluentValidation.AspNetCore`, `Microsoft.EntityFrameworkCore.SqlServer`, `Hangfire.AspNetCore`, `Hangfire.SqlServer`, `Serilog.AspNetCore`, `Serilog.Sinks.ApplicationInsights`, `Azure.Identity`, `Microsoft.AspNetCore.Authentication.JwtBearer`, `OpenTelemetry.Extensions.Hosting`, `Azure.Monitor.OpenTelemetry.Exporter`, `BCrypt.Net-Next`
- [X] T004 [P] Add NuGet packages to `src/PoTraffic.Client`: `Radzen.Blazor`, `Microsoft.AspNetCore.Components.WebAssembly`
- [X] T005 [P] Add NuGet packages to `src/PoTraffic.Shared`: no third-party dependencies (pure DTOs/enums/constants)
- [X] T006 [P] Add NuGet packages to test projects: `xunit`, `xunit.runner.visualstudio`, `NSubstitute`, `FluentAssertions`, `Testcontainers.MsSql` (3.x), `Microsoft.AspNetCore.Mvc.Testing`, `Microsoft.Playwright` to `PoTraffic.E2ETests`
- [X] T007 Create `docker-compose.yml` at repo root with `mcr.microsoft.com/azure-sql-edge` (ARM64-compatible) and `mcr.microsoft.com/azure-storage/azurite` services; expose SQL on port 1433
- [X] T008 [P] Create `.github/workflows/ci.yml` with build + unit-test + integration-test + E2E-test jobs skeleton (trigger: `push`, `pull_request` to `main`)
- [X] T009 Bootstrap Serilog in `src/PoTraffic.Api/Program.cs` as the sole MEL backend via `AddSerilog()`; configure `WriteTo.Console()` and `WriteTo.ApplicationInsights()` sinks; all app code depends on `ILogger<T>` only
- [X] T010 [P] Create `src/PoTraffic.Api/Infrastructure/Observability/CompositeRoutingSampler.cs` skeleton: `AlwaysOnSampler` for user-facing traces, `TraceIdRatioBased(0.5)` for Hangfire background traces
- [X] T011 [P] Create `appsettings.json`, `appsettings.Development.json`, and `appsettings.Testing.json` in `src/PoTraffic.Api`; document user-secrets structure for `ConnectionStrings__DefaultDB`, `Jwt__Key`, `TomTom__ApiKey`, `GoogleMaps__ApiKey`
- [X] T012 [P] Verify `docs/tech-context.md` is current (already created by `@sddp.plan`); confirm it is linked from `README.md` and its path is correctly registered in `.github/sddp-config.md`

---
<!-- ✅ Checkpoint: Solution builds, Docker Compose services start, CI pipeline skeleton passes -->

## Phase 2 — Foundational ⚠️ BLOCKS ALL STORIES

> EF Core DbContext + all entity configurations + initial migration + seed data, Hangfire bootstrap, JWT auth, OTel sampler, client-log endpoint, testing endpoints, Shared DTOs/enums skeleton, integration test base, Program.cs wiring.

- [X] T013 Create `src/PoTraffic.Api/Infrastructure/Data/PoTrafficDbContext.cs` with `DbSet<T>` for all entities; configure `HasDefaultSchema("dbo")`; register `BaselineSlotDto` and `UserDailyUsageDto` via `HasNoKey()`
- [X] T014 [P] Create `User` entity class and EF Core fluent configuration: `NEWSEQUENTIALID()` default PK, `UX_Users_Email` unique index, `nvarchar(320)` email, `IsGdprDeleteRequested` default `false`
- [X] T015 [P] Create `Route` entity class and EF Core fluent configuration: `NEWSEQUENTIALID()` PK, `FK_Routes_Users` cascade delete, `IX_Routes_UserId` composite index on `(UserId, MonitoringStatus)`, `Provider` and `MonitoringStatus` mapped via `HasConversion<int>()`
- [X] T016 [P] Create `MonitoringWindow` entity class and EF Core fluent configuration: `FK_MonitoringWindows_Routes` cascade delete, `StartTime`/`EndTime` mapped as `TimeOnly` with `HasColumnType("time(0)")`, `DaysOfWeekMask` as `byte`
- [X] T017 [P] Create `MonitoringSession` entity class and EF Core fluent configuration: `FK_MonitoringSessions_Routes` cascade delete, `SessionDate` mapped as `DateOnly` with `HasColumnType("date")`, unique index `IX_MonitoringSessions_RouteId_SessionDate`, `State` mapped via `HasConversion<int>()`
- [X] T018 [P] Create `PollRecord` entity class and EF Core fluent configuration: `NEWSEQUENTIALID()` PK, `HasQueryFilter(p => !p.IsDeleted)` global query filter, `FK_PollRecords_Routes` cascade delete, `FK_PollRecords_Sessions` `OnDelete(DeleteBehavior.SetNull)`, `RawProviderResponse` as `nvarchar(max)`, indexes on `(RouteId, PolledAt DESC)`, `SessionId`, `PolledAt`
- [X] T019 [P] Create `PublicHoliday` entity class and EF Core fluent configuration: `int IDENTITY(1,1)` PK via `UseIdentityColumn()`, unique index `UX_PublicHolidays_Locale_Date` on `(Locale, HolidayDate)`
- [X] T020 [P] Create `SystemConfiguration` entity class and EF Core fluent configuration: string PK `HasMaxLength(100)`, `HasData(...)` seed rows for `cost.perpoll.googlemaps`, `cost.perpoll.tomtom`, `quota.daily.default`, `quota.reset.utc`
- [X] T021 [P] Create `BaselineSlotDto` and `UserDailyUsageDto` read-projection records in `src/PoTraffic.Api/Infrastructure/Data/`; register both via `HasNoKey()` in `OnModelCreating`
- [X] T022 Run `dotnet ef migrations add InitialCreate` to generate `src/PoTraffic.Api/Infrastructure/Data/Migrations/` with all entity tables, indexes, FKs, and `SystemConfiguration` seed data
- [X] T023 Create `src/PoTraffic.Shared/Enums/RouteProvider.cs` (`GoogleMaps = 0`, `TomTom = 1`), `MonitoringStatus.cs` (`Active = 0`, `Paused = 1`, `Deleted = 2`), `SessionState.cs` (`Pending = 0`, `Active = 1`, `Completed = 2`)
- [X] T024 [P] Create `src/PoTraffic.Shared/Constants/ValidationConstants.cs` (max lengths, regex patterns) and `QuotaConstants.cs` (`DefaultDailyQuota = 10`, `RerouteDistanceThresholdPercent = 15`, `BaselineMinSessionCount = 3`)
- [X] T025 [P] Create `src/PoTraffic.Shared/DTOs/` folder skeleton with empty placeholder files in `Auth/`, `Routes/`, `History/`, `Account/`, `Admin/` subfolders (populated per user-story phase)
- [X] T026 Create `src/PoTraffic.Api/Infrastructure/Hangfire/HangfireJobActivator.cs`: DI bridge implementing `JobActivator` using `IServiceScopeFactory`; comment: `// Adapter pattern — bridges Hangfire job activation to ASP.NET Core DI scope lifecycle`
- [X] T027 [P] Create `src/PoTraffic.Api/Infrastructure/Security/JwtConfiguration.cs` (typed options binding `Jwt:Key`, `Jwt:Issuer`, `Jwt:Audience`, `Jwt:ExpiryMinutes`); wire `AddAuthentication().AddJwtBearer(...)` in `Program.cs`
- [X] T028 [P] Create `src/PoTraffic.Api/Infrastructure/Logging/ClientLogEndpoint.cs`: `POST /api/client-logs` accepts `ClientLogBatchRequest`; deserialises and forwards each entry to `ILogger<ClientLogEndpoint>`; returns `204`
- [X] T029 [P] Create `src/PoTraffic.Api/Infrastructure/Testing/TestingEndpoints.cs`: register `GET /e2e/dev-login` and `POST /e2e/seed` **only** inside `if (app.Environment.IsEnvironment("Testing"))`; returns `404` by route-non-existence in all other environments; comment: `// Factory pattern — conditionally registers test infrastructure endpoints`
- [X] T030 [P] Create `src/PoTraffic.Client/Infrastructure/Http/ApiClientBase.cs`: base `HttpClient` wrapper handling JWT `Authorization` header injection and `ProblemDetails` error deserialization
- [X] T031 Wire `src/PoTraffic.Api/Program.cs`: EF Core SQL Server, Hangfire (SQL Server storage, `HangfireJobActivator`), MediatR assembly scan, FluentValidation, JWT auth, Identity, OTel with `CompositeRoutingSampler`, Serilog, all endpoint groups, `UseHangfireDashboard`, `UseHangfireServer`
- [X] T032 Create `tests/PoTraffic.IntegrationTests/BaseIntegrationTest.cs`: Testcontainers `MsSqlContainer` (3.x), `WebApplicationFactory<Program>` override pointing `ConnectionStrings__DefaultDB` at the container; `IAsyncLifetime` for container start/stop; `ApplyMigrationsAsync()` helper
- [X] T033 [P] Create `src/PoTraffic.Client/Program.cs`: Blazor WASM bootstrap; register `ApiClientBase`, Radzen services, auth state provider, `ILoggerProvider` → `WasmForwardingLoggerProvider` (stub at this phase)

---
<!-- ✅ Checkpoint: dotnet build passes, EF migration runs against SQL Edge, integration test base class connects to Testcontainers instance -->

## Phase 3 — US1: Monitor a Route Automatically (P1) 🎯 MVP

> Tests written first (Red-Green-Refactor). Covers `Routes/`, `MonitoringWindows/`, `PollRouteJob`, `ExecutePollCommand`, rolling 5-min chain, quota enforcement, reroute flag persistence.

- [X] T034 Write unit test: `ExecutePollHandler` records `TravelDurationSeconds`, `DistanceMetres`, `PolledAt`, `Provider` on a successful provider response — `src/tests/PoTraffic.UnitTests/Features/Routes/ExecutePollHandlerTests.cs`
- [X] T035 [P] Write unit test: reroute detection — two consecutive readings ≥15% above median set `IsRerouted = true`; single anomalous reading does not — `tests/PoTraffic.UnitTests/Features/Routes/RerouteDetectionTests.cs`
- [X] T036 [P] Write unit test: `StartWindowHandler` returns quota-exceeded result when `MonitoringSessions` count for today equals `QuotaConstants.DefaultDailyQuota` — `tests/PoTraffic.UnitTests/Features/MonitoringWindows/StartWindowHandlerTests.cs`
- [X] T037 [P] Write unit test: `StopWindowCommand` handler transitions session `State` → `Completed` and deletes the Hangfire chain job — `tests/PoTraffic.UnitTests/Features/MonitoringWindows/WindowLifecycleTests.cs`
- [X] T038 [P] Write unit test: `CreateRouteValidator` rejects routes where origin and destination resolve to the same address string (FR-014) — `tests/PoTraffic.UnitTests/Features/Routes/CreateRouteValidatorTests.cs`
- [X] T039 Write integration test: `POST /api/routes` creates a `Route` row; `POST /api/routes/{id}/windows` creates a `MonitoringWindow` row — `tests/PoTraffic.IntegrationTests/Features/Routes/CreateRouteIntegrationTests.cs`
- [X] T040 [P] Write integration test: `POST /api/routes/{id}/windows/{wid}/start` with quota exhausted returns `429` and no new `MonitoringSession` is created — `tests/PoTraffic.IntegrationTests/Features/MonitoringWindows/StartWindowIntegrationTests.cs`
- [X] T041 [P] Write integration test: `PollRouteJob.Execute` inserts a `PollRecord` with `SessionId` set and increments `MonitoringSession.PollCount` — `tests/PoTraffic.IntegrationTests/Features/Routes/PollRouteJobIntegrationTests.cs`
- [X] T042 Write E2E scenario: trigger `/e2e/seed` to create route + window, call `/e2e/dev-login`, start monitoring, assert poll records appear in DB within test timeout — `tests/PoTraffic.E2ETests/Scenarios/RouteMonitoringScenarios.cs`
- [X] T043 Create `src/PoTraffic.Api/Features/Routes/CreateRouteCommand.cs` + `Handler` + `Validator`: address verification call via `ITrafficProvider`, same-coords check (FR-014), persist `Route`; comment: `// Strategy pattern — ITrafficProvider swaps mapping API per route.Provider`
- [X] T044 [P] Create `src/PoTraffic.Api/Features/Routes/UpdateRouteCommand.cs` + `Handler` + `Validator`: re-verify addresses on address fields change; cancel + restart Hangfire chain if provider changes
- [X] T045 [P] Create `src/PoTraffic.Api/Features/Routes/DeleteRouteCommand.cs` + `Handler`: call `BackgroundJob.Delete(route.HangfireJobChainId)` before `DbContext.Remove(route)`; cascade handled by FK
- [X] T046 [P] Create `src/PoTraffic.Api/Features/Routes/GetRoutesQuery.cs` + `Handler`: return paginated list of routes for authenticated user; project to `src/PoTraffic.Shared/DTOs/Routes/RouteDto.cs`
- [X] T047 Create `src/PoTraffic.Api/Features/Routes/ExecutePollCommand.cs` + `Handler`: load `Route` + active `MonitoringSession`; call `ITrafficProvider`; evaluate reroute rule against two prior `PollRecords` for same `RouteId`+`SessionId` (FR-006); insert `PollRecord`; update `MonitoringSession.LastPollAt` and `PollCount`
- [X] T048 [P] Create `src/PoTraffic.Api/Features/Routes/PollRouteJob.cs`: thin Hangfire job dispatcher; inject `IServiceScopeFactory`; resolve `ISender` in scope; dispatch `ExecutePollCommand`; call `BackgroundJob.Schedule(self, TimeSpan.FromMinutes(5))` in `finally`; store returned job ID on `Route.HangfireJobChainId`; comment: `// Chain of Responsibility pattern — each job enqueues its own successor`
- [X] T049 [P] Create `src/PoTraffic.Api/Features/MonitoringWindows/CreateWindowCommand.cs` + `Handler` + `Validator`: validate `EndTime > StartTime`, at least one `DaysOfWeekMask` bit set; enforce one-active-window-per-route (return `409 Conflict` if an active `MonitoringWindow` already exists for the `RouteId`); persist `MonitoringWindow`
- [X] T050 [P] Create `src/PoTraffic.Api/Features/MonitoringWindows/StartWindowCommand.cs` + `Handler`: count today's sessions for user; enforce quota (FR-003); create `MonitoringSession` with `State = Active`; schedule first `PollRouteJob` via `BackgroundJob.Schedule(..., TimeSpan.Zero)`; check `PublicHolidays` for user locale + session date to set `IsHolidayExcluded`; return `quotaRemaining` in response
- [X] T051 [P] Create `src/PoTraffic.Api/Features/MonitoringWindows/StopWindowCommand.cs` + `Handler`: update `MonitoringSession.State = Completed`; call `BackgroundJob.Delete(route.HangfireJobChainId)`; null out `Route.HangfireJobChainId`
- [X] T052 [P] Create `src/PoTraffic.Api/Features/Routes/RoutesEndpoints.cs`: Minimal API group `/api/routes`; `GET`, `POST`, `PUT /{id}`, `DELETE /{id}`, `POST /{id}/check-now`, `POST /{id}/verify-address`; require auth
- [X] T053 [P] Create `src/PoTraffic.Api/Features/MonitoringWindows/WindowsEndpoints.cs`: Minimal API group `/api/routes/{routeId}/windows`; `GET`, `POST`, `PUT /{id}`, `DELETE /{id}`, `POST /{id}/start`, `POST /{id}/stop`; require auth
- [X] T054 [P] Create `ITrafficProvider` interface + `GoogleMapsTrafficProvider` and `TomTomTrafficProvider` implementations in `src/PoTraffic.Api/Infrastructure/Providers/`; register as keyed services by `RouteProvider` enum; comment: `// Strategy pattern — swaps traffic data source per route provider setting`
- [X] T122 [P] [US1] Write unit test: `ExecutePollHandler` catches provider exception → logs warning via `ILogger` → no `PollRecord` inserted → `MonitoringSession.PollCount` unchanged → no exception propagates to Hangfire caller (FR-005) — `tests/PoTraffic.UnitTests/Features/Routes/ExecutePollHandlerFailureTests.cs`
- [X] T123 [P] [US1] Write parameterised unit theory: inject a 20-record synthetic `PollRecord` sequence with 4 known reroutes and 16 normal-variation readings; assert reroute detection flags ≥19 of 20 correctly (≥95% accuracy, SC-004) — `tests/PoTraffic.UnitTests/Features/Routes/RerouteAccuracyTheoryTests.cs`

---
<!-- ✅ Checkpoint: All Phase 3 unit and integration tests pass; PollRouteJob chain visible in Hangfire dashboard -->

## Phase 4 — US2: View the Volatility Dashboard (P2)

> Tests written first. Covers `History/` slice, Blazor `DashboardPage`, `RouteDetailPage` with dual-series Radzen chart, `PeriodicTimer`, delta shading, reroute tooltip, and optimal departure window.

- [X] T055 Write unit test: `GetBaselineHandler` returns `null` for `StdDevDurationSeconds` slots when fewer than 3 distinct sessions contribute to a 5-minute bucket — `tests/PoTraffic.UnitTests/Features/History/GetBaselineHandlerTests.cs`
- [X] T056 [P] Write unit test: `GetPollHistoryHandler` returns only records for the authenticated user's route; paginates correctly by `page` and `pageSize` — `tests/PoTraffic.UnitTests/Features/History/GetPollHistoryHandlerTests.cs`
- [X] T057 Write integration test: `GET /api/routes/{id}/baseline` with 3+ seeded same-weekday sessions returns `BaselineSlotDto` rows with non-null `StdDevDurationSeconds` and `MeanDurationSeconds`; with fewer than 3 sessions the endpoint returns an empty array — `tests/PoTraffic.IntegrationTests/Features/History/BaselineIntegrationTests.cs`
- [X] T058 Write E2E scenario: seed route with historical + current-session poll data; verify `DashboardPage` renders a Radzen chart with two series and delta shading — `tests/PoTraffic.E2ETests/Scenarios/RouteMonitoringScenarios.cs`
- [X] T059 Create `src/PoTraffic.Api/Features/History/GetPollHistoryQuery.cs` + `Handler`: paginated `PollRecord` list for `routeId`; project to `src/PoTraffic.Shared/DTOs/History/PollRecordDto.cs`; `IsRerouted` flag included
- [X] T060 [P] Create `src/PoTraffic.Api/Features/History/GetBaselineQuery.cs` + `Handler`: parameterised `SqlQueryRaw<BaselineSlotDto>` per §6.2 of data-model; `HAVING COUNT(DISTINCT date) >= 3`; exclude `IsHolidayExcluded` sessions; 90-day window filter; all params as `SqlParameter`
- [X] T061 [P] Create `src/PoTraffic.Api/Features/History/GetSessionsQuery.cs` + `Handler`: list of `MonitoringSession` rows for a route, ordered by `SessionDate DESC`; project to `src/PoTraffic.Shared/DTOs/History/SessionDto.cs`
- [X] T062 [P] Create `src/PoTraffic.Api/Features/History/HistoryEndpoints.cs`: Minimal API group `/api/routes/{routeId}`; `GET /poll-history`, `GET /baseline`, `GET /sessions`; require auth
- [X] T063 [P] Create `src/PoTraffic.Shared/DTOs/History/PollRecordDto.cs`, `BaselineSlotDto.cs`, `SessionDto.cs`, `BaselineResponse.cs` (wraps slots with `HasSufficientData` bool)
- [X] T064 Create `src/PoTraffic.Client/Features/Dashboard/DashboardPage.razor` + `DashboardViewModel.cs`: quota indicator (`consumed / 10`) with Radzen `Alert` warning at ≥ 8; active/most-recent session selector; navigates to `RouteDetailPage`; `PeriodicTimer` refresh every 60 s
- [X] T065 [P] Create `src/PoTraffic.Client/Features/Routes/RouteDetailPage.razor`: dual-series `RadzenChart` (`Today's Actual` + `Historical Baseline`); `±1σ` variance band as area series; delta shading with Tax/Bonus fill colours; reroute tooltip (FR-011); optimal departure window label bound to `GetOptimalDepartureQuery` result (FR-009); `PeriodicTimer` poll every 60 s; `IAsyncDisposable.DisposeAsync` disposes timer; comment: `// Observer pattern — PeriodicTimer drives UI refresh without SignalR dependency`
- [X] T116 [US2] Write unit test: `GetOptimalDepartureHandler` returns the correct contiguous departure window from a seeded baseline of known slot averages; when qualifying slots are non-contiguous returns only the longest run — `tests/PoTraffic.UnitTests/Features/History/GetOptimalDepartureHandlerTests.cs`
- [X] T117 [P] [US2] Create `src/PoTraffic.Api/Features/History/GetOptimalDepartureQuery.cs` + `Handler`: load `BaselineSlotDto` rows for route + weekday; find contiguous run of slots whose `MeanDurationSeconds` falls within 5% of the minimum; return `{windowStart, windowEnd, label}` (e.g., `"Best: 08:05–08:20"`); return `null` when fewer than 3 sessions exist (FR-009, FR-012); add to `HistoryEndpoints.cs` as `GET /api/routes/{id}/optimal-departure`; add `OptimalDepartureDto` to `src/PoTraffic.Shared/DTOs/History/`

---
<!-- ✅ Checkpoint: Dashboard renders dual-series chart; Phase 4 integration test passes with STDEV result from SQL Edge -->

## Phase 5 — US3: Manage Routes and Perform Quick Checks (P3)

> Tests written first. Covers Auth slice, Route CRUD UI, address verification, Check Now transient notification.

- [X] T066 Write unit test: `RegisterCommandValidator` rejects duplicate email (mock `IUserRepository`); `LoginCommandValidator` rejects empty password — `tests/PoTraffic.UnitTests/Features/Auth/AuthValidatorTests.cs`
- [X] T067 [P] Write unit test: `VerifyAddressCommand` handler returns `ValidationProblem` when provider returns identical coordinates for origin and destination (FR-014) — `tests/PoTraffic.UnitTests/Features/Routes/VerifyAddressHandlerTests.cs`
- [X] T068 Write integration test: `POST /api/auth/register` creates a user; `POST /api/auth/login` returns a valid JWT; `POST /api/auth/refresh-token` returns a new access token — `tests/PoTraffic.IntegrationTests/Features/Auth/AuthIntegrationTests.cs`
- [X] T069 [P] Write integration test: authenticated `GET /api/routes` returns empty list; `POST /api/routes` creates route; `DELETE /api/routes/{id}` removes it; `POST /api/routes/{id}/check-now` returns duration without inserting `PollRecord` — `tests/PoTraffic.IntegrationTests/Features/Routes/RouteCrudIntegrationTests.cs`
- [X] T070 Write E2E scenario: register account → login → add route with address verification → perform "Check Now" → assert transient notification appears and no new DB row — `tests/PoTraffic.E2ETests/Scenarios/AuthScenarios.cs`
- [X] T071 Create `src/PoTraffic.Api/Features/Auth/RegisterCommand.cs` + `Handler` + `Validator`: hash password with BCrypt; generate GUID `EmailVerificationToken`, persist on `User`; emit token to `ILogger<RegisterHandler>` at `Information` level (MVP: no SMTP — `"Email verification token for {Email}: {Token}"`) so developers see it in the Serilog console sink; add `POST /api/auth/confirm-email?token={token}` stub endpoint that sets `User.IsEmailVerified = true` and returns `204`; return `201` with `AuthResponse`; Administrator role is not self-registrable (FR-029)
- [X] T072 [P] Create `src/PoTraffic.Api/Features/Auth/LoginCommand.cs` + `Handler` + `Validator`: verify BCrypt hash; issue signed JWT (`JwtConfiguration`); update `User.LastLoginAt`; return `AuthResponse` with `accessToken` + `refreshToken`
- [X] T073 [P] Create `src/PoTraffic.Api/Features/Auth/RefreshTokenCommand.cs` + `Handler`: validate refresh token; issue new access token; rotate refresh token
- [X] T074 [P] Create `src/PoTraffic.Api/Features/Auth/AuthEndpoints.cs`: Minimal API group `POST /api/auth/register`, `/login`, `/logout`, `/refresh-token`; anonymous access
- [X] T075 [P] Create `src/PoTraffic.Shared/DTOs/Auth/RegisterRequest.cs`, `LoginRequest.cs`, `AuthResponse.cs` (accessToken, refreshToken, expiresAt, userId, role)
- [X] T076 Create `src/PoTraffic.Client/Features/Auth/LoginPage.razor`: Radzen `RadzenLogin` form; error message binding; redirect to Dashboard on success
- [X] T077 [P] Create `src/PoTraffic.Client/Features/Auth/RegisterPage.razor`: Radzen form; email + password + confirm fields; validation messages; redirect to Login on success
- [X] T078 Create `src/PoTraffic.Client/Features/Routes/RouteListPage.razor`: Radzen `RadzenDataGrid` showing origin, destination, window days/times, provider; inline add/edit panel (FR-018); `Delete` with `RadzenConfirmDialog`; "Check Now" button → transient `RadzenNotification`
- [X] T079 [P] Create `src/PoTraffic.Client/Features/Routes/CreateRoutePage.razor`: address free-text inputs with "Verify" button calling `POST /api/routes/verify-address`; provider `RadzenDropDown`; save triggers `POST /api/routes`
- [X] T080 [P] Create `src/PoTraffic.Client/Features/MonitoringWindows/WindowConfigPanel.razor`: day-of-week bitmask checkbox group; `RadzenTimePicker` for start/end; embedded within `RouteListPage` inline panel
- [X] T081 Add `CheckNowCommand` + `Handler` and `VerifyAddressCommand` + `Handler` under `src/PoTraffic.Api/Features/Routes/`; `CheckNow` calls `ITrafficProvider`, returns `CheckNowResponse` DTO, writes no `PollRecord` and consumes no quota (FR-016)
- [X] T082 Create `tests/PoTraffic.E2ETests/Helpers/TestingApiClient.cs`: typed HTTP client wrapping `POST /e2e/dev-login` (returns JWT) and `POST /e2e/seed` (seeds test data); used by all E2E scenarios

---
<!-- ✅ Checkpoint: Register/Login E2E scenario passes; Route CRUD integration tests pass; "Check Now" returns result with no DB write -->

## Phase 6 — US4: Automated Data Maintenance (P4)

> Tests written first. Nightly scheduled purge only; reroute detection is already implemented in Phase 3.

- [X] T083 Write unit test: `PruneOldPollRecordsJob` marks `IsDeleted = true` for records with `PolledAt < today - 90 days`, does not touch records within the 90-day window, and returns the deleted count for logging — `tests/PoTraffic.UnitTests/Features/Maintenance/PruningJobTests.cs`
- [X] T084 Write integration test: seed 5 `PollRecords` with `PolledAt = today - 91 days` and 3 with `PolledAt = today - 89 days`; trigger `PruneOldPollRecordsJob` directly; assert 5 rows are soft-deleted and 3 rows are untouched — `tests/PoTraffic.IntegrationTests/Features/Maintenance/PruningIntegrationTests.cs`
- [X] T085 Create `src/PoTraffic.Api/Features/Maintenance/PruneOldPollRecordsJob.cs` + `Handler`: Hangfire `RecurringJob`; call `IgnoreQueryFilters()` to bypass global filter; batch-update `IsDeleted = true` for `PolledAt < GETUTCDATE() - 90`; null out `RawProviderResponse` in same batch; log deleted row count (FR-020); comment: `// Command pattern — encapsulates nightly batch mutation as a discrete MediatR command`
- [X] T086 [P] Register `RecurringJob.AddOrUpdate<PruneOldPollRecordsJob>` in `src/PoTraffic.Api/Program.cs` with cron expression `"0 2 * * *"` (02:00 UTC nightly); quota is derived dynamically by counting `MonitoringSessions WHERE SessionDate = today_utc` — no counter column exists to reset, no separate quota-reset job required (F-004 fix)

---
<!-- ✅ Checkpoint: Integration test asserts row-count reduction; pruning job visible in Hangfire recurring jobs -->

## Phase 7 — US5: Admin Dashboard and Diagnostics (P5)

> Tests written first. Admin usage table, aggregated volatility view, /Diag masking, `[AdminOnly]` policy.

- [X] T087 Write unit test: `GetPollCostSummaryHandler` computes `EstimatedCostUsd = TodayPollCount × cost-per-provider` using `SystemConfiguration` values; verifies per-provider rate lookup — `tests/PoTraffic.UnitTests/Features/Admin/GetPollCostSummaryTests.cs`
- [X] T088 Write integration test: non-admin JWT receives `403` on `GET /api/admin/users`, `GET /api/admin/system-configuration`, and `GET /api/admin/poll-cost-summary` — `tests/PoTraffic.IntegrationTests/Features/Admin/AdminAuthorizationIntegrationTests.cs`
- [X] T089 Write E2E scenario: admin logs in via `/e2e/dev-login`, navigates to Admin page; asserts usage table shows seeded users with correct poll counts — `tests/PoTraffic.E2ETests/Scenarios/AdminDashboardScenarios.cs`
- [X] T090 Create `src/PoTraffic.Api/Features/Admin/GetUsersQuery.cs` + `Handler`: `SqlQueryRaw<UserDailyUsageDto>` per §6.3 of data-model; GROUP BY `(u.Id, u.Email)` filtered to today's UTC date; project to `src/PoTraffic.Shared/DTOs/Admin/UserDailyUsageDto.cs`
- [X] T091 [P] Create `src/PoTraffic.Api/Features/Admin/GetSystemConfigurationQuery.cs` + `Handler`: load all `SystemConfiguration` rows; mask `IsSensitive = true` values as `first2 + "****" + last2` (FR-026); project to `src/PoTraffic.Shared/DTOs/Admin/SystemConfigDto.cs`
- [X] T092 [P] Create `src/PoTraffic.Api/Features/Admin/UpdateSystemConfigurationCommand.cs` + `Handler` + `Validator`: update `SystemConfiguration.Value` by key; admin-only
- [X] T093 [P] Create `src/PoTraffic.Api/Features/Admin/GetPollCostSummaryQuery.cs` + `Handler`: join `UserDailyUsageDto` result with `SystemConfiguration` cost-per-provider entries in application layer; compute `EstimatedCostUsd` per user; project to `src/PoTraffic.Shared/DTOs/Admin/PollCostSummaryDto.cs`
- [X] T094 [P] Create `src/PoTraffic.Api/Features/Admin/AdminEndpoints.cs`: Minimal API group `/api/admin`; `GET /users`, `GET /system-configuration`, `PUT /system-configuration/{key}`, `GET /poll-cost-summary`; apply `[AdminOnly]` authorization policy
- [X] T095 [P] Create `src/PoTraffic.Shared/DTOs/Admin/UserDailyUsageDto.cs`, `SystemConfigDto.cs`, `PollCostSummaryDto.cs`
- [X] T096 Create `src/PoTraffic.Client/Features/Admin/AdminUsagePage.razor`: `RadzenDataGrid` listing `UserDailyUsageDto` columns (email, poll count, estimated cost); refresh button; visible to Administrator role only
- [X] T097 [P] Create `src/PoTraffic.Client/Features/Admin/DiagPage.razor`: render `SystemConfigDto` list as formatted key-value pairs with `IsSensitive` values displayed masked; `/Diag` route; Administrator role guard
- [X] T118 [US5] Write unit test: `GetGlobalVolatilityHandler` groups `PollRecord` rows by `(RouteId, TimeSlotBucket)` across all users and returns aggregated mean duration; verifies no individual user data is exposed in the projection — `tests/PoTraffic.UnitTests/Features/Admin/GetGlobalVolatilityHandlerTests.cs`
- [X] T119 [P] [US5] Create `src/PoTraffic.Api/Features/Admin/GetGlobalVolatilityQuery.cs` + `Handler`: `SqlQueryRaw<GlobalVolatilitySlotDto>` aggregating `AVG(TravelDurationSeconds)` grouped by `TimeSlotBucket` across all users within 90-day window; project to `src/PoTraffic.Shared/DTOs/Admin/GlobalVolatilitySlotDto.cs`
- [X] T120 [P] [US5] Add `GET /api/admin/global-volatility` to `src/PoTraffic.Api/Features/Admin/AdminEndpoints.cs`; `[AdminOnly]`; dispatches `GetGlobalVolatilityQuery`; add `GlobalVolatilitySlotDto` to `src/PoTraffic.Shared/DTOs/Admin/`
- [X] T121 [P] [US5] Create `src/PoTraffic.Client/Features/Admin/GlobalVolatilityPage.razor`: `RadzenChart` area series showing aggregate mean duration per 5-minute slot across all users; Administrator role guard; linked from Admin nav (FR-024)
- [X] T126 [P] [US5] Write integration test: `GET /api/admin/system-configuration` response for a known `IsSensitive = true` key matches regex `^.{2}\*{4}.{2}$` (first 2 + masked middle + last 2, SC-009) — `tests/PoTraffic.IntegrationTests/Features/Admin/SensitiveMaskingIntegrationTests.cs`

---
<!-- ✅ Checkpoint: Admin 403 integration test passes; Admin usage table renders in E2E; /Diag masking verified -->

## Phase 8 — US5 continued: Account and Settings (P5)

> Tests written first. Profile, password change, quota query, GDPR hard-delete cascade.

- [X] T098 Write unit test: `DeleteAccountHandler` issues a single hard `DELETE` on the `User` entity and verifies cascade teardown is not duplicated in application code (schema FKs handle descendant rows) — `tests/PoTraffic.UnitTests/Features/Account/DeleteAccountCommandTests.cs`
- [X] T099 Write integration test: `DELETE /api/account` removes the user row; subsequent `GET /api/routes` with same user JWT returns `401`; no orphaned `Route`, `MonitoringSession`, or `PollRecord` rows remain for that user — `tests/PoTraffic.IntegrationTests/Features/Account/DeleteAccountIntegrationTests.cs`
- [X] T100 Write E2E scenario: log in, navigate to Settings, click "Delete Account", confirm dialog, assert redirect to login page and re-login attempt fails — `tests/PoTraffic.E2ETests/Scenarios/AuthScenarios.cs`
- [X] T101 Create `src/PoTraffic.Api/Features/Account/GetProfileQuery.cs` + `Handler`: return `ProfileDto` (email, locale, createdAt, role)
- [X] T102 [P] Create `src/PoTraffic.Api/Features/Account/UpdateProfileCommand.cs` + `Handler` + `Validator`: allow `Locale` update (IANA tz string); validate format
- [X] T103 [P] Create `src/PoTraffic.Api/Features/Account/ChangePasswordCommand.cs` + `Handler` + `Validator`: verify current password via BCrypt; hash and persist new password
- [X] T104 Create `src/PoTraffic.Api/Features/Account/DeleteAccountCommand.cs` + `Handler`: hard `DELETE FROM Users WHERE Id = @userId`; schema CASCADE handles all descendant rows; irreversible; comment: `// GDPR Art. 17 — hard-delete; no soft-delete or deferred queue permitted (FR-031)`
- [X] T105 [P] Create `src/PoTraffic.Api/Features/Account/GetQuotaQuery.cs` + `Handler`: count `MonitoringSessions` where `SessionDate = today UTC` for caller; return `{consumed, total: 10, remaining}` as `QuotaDto`
- [X] T106 [P] Create `src/PoTraffic.Api/Features/Account/AccountEndpoints.cs`: Minimal API group `/api/account`; `GET /profile`, `PUT /profile`, `PUT /password`, `GET /quota`, `DELETE` (GDPR); require auth
- [X] T107 [P] Create `src/PoTraffic.Shared/DTOs/Account/ProfileDto.cs`, `UpdateProfileRequest.cs`, `ChangePasswordRequest.cs`, `QuotaDto.cs`
- [X] T108 Create `src/PoTraffic.Client/Features/Account/SettingsPage.razor`: `RadzenTabs` with Profile, Password, and Quota sections; GDPR Delete button shows `RadzenConfirmDialog` before dispatching `DELETE /api/account`; Quota section shows `RadzenProgressBar` (consumed / 10)

---
<!-- ✅ Checkpoint: Delete account E2E scenario passes; cascade asserted in integration test; no orphaned rows -->

## Phase 9 — Polish

> WASM log forwarding, Playwright E2E wiring, Hangfire dashboard guard, PublicHoliday seeding, README, and canary integration tests.

- [X] T109 Implement `src/PoTraffic.Client/Infrastructure/Logging/WasmForwardingLoggerProvider.cs`: `ILoggerProvider` that batches log entries and forwards them via `HttpClient` to `POST /api/client-logs`; uses `System.Threading.Channels` for non-blocking batching; dispose flushes pending batch; comment: `// Adapter pattern — adapts MEL ILoggerProvider to HTTP sink for WASM structured log forwarding`
- [X] T110 Wire Playwright full E2E suite in `tests/PoTraffic.E2ETests/`: configure `playwright.config.json`; ensure `AuthScenarios`, `RouteMonitoringScenarios`, `AdminDashboardScenarios` all use `TestingApiClient` for seed/login; register `IPlaywright` + `IBrowser` in `IAsyncLifetime` base class
- [X] T111 [P] Add Hangfire dashboard auth guard in `src/PoTraffic.Api/Program.cs`: `UseHangfireDashboard("/hangfire", new DashboardOptions { Authorization = [new HangfireAdminAuthorizationFilter()] })`; create `HangfireAdminAuthorizationFilter.cs` in `Infrastructure/Hangfire/`; deny access to non-Administrator roles
- [X] T112 [P] Add `PublicHoliday` seed data migration for common locales (`Europe/London` UK bank holidays 2026–2027, `America/New_York` US federal holidays 2026–2027, `Europe/Warsaw` Polish public holidays 2026–2027) in `src/PoTraffic.Api/Infrastructure/Data/Migrations/`
- [X] T113 [P] Write integration test: `GET /e2e/dev-login` returns `404` (not `401`) when `ASPNETCORE_ENVIRONMENT = Production`; asserts route is not registered — `tests/PoTraffic.IntegrationTests/Infrastructure/TestingEndpointSecurityTests.cs`
- [X] T114 [P] Write integration test: OTel sampling canary — assert that synthetic Hangfire-tagged `Activity` objects created in a tight loop yield a sample rate of approximately 50% (±10%) over 200 iterations — `tests/PoTraffic.IntegrationTests/Infrastructure/ObservabilityTests.cs`
- [X] T115 Update `README.md` at repo root with Quickstart steps from `specs/00001-potraffic-core/quickstart.md`: Docker Compose setup, `dotnet ef database update`, `dotnet user-secrets set`, unit/integration/E2E test commands, Hangfire dashboard URL
- [X] T124 [P] Write Playwright E2E mobile viewport smoke test: set `viewport: { width: 375, height: 667 }`; assert Dashboard page loads, primary nav links are visible, route grid and Check Now button are accessible without horizontal scrolling (FR-032) — `tests/PoTraffic.E2ETests/Scenarios/MobileViewportScenarios.cs`
- [X] T125 [P] Write integration test: seed 90 days of `PollRecord` rows for a single route (full window, 12 polls/day); assert `GET /api/routes/{id}/baseline` completes within 200ms (SC-002 server-side target) — `tests/PoTraffic.IntegrationTests/Features/History/BaselinePerformanceTests.cs`

---
<!-- ✅ Checkpoint: All 126 tasks complete; CI pipeline green across all test layers; Hangfire dashboard accessible to admin only; /e2e/dev-login returns 404 in Production -->






