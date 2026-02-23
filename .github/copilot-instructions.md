# PoTraffic Project Instructions

## Core Principles

### I. Zero-Waste Codebase
Every PR merged to `main` MUST leave the codebase cleaner than it was found (Boy Scout Rule). Unused files, dead code, commented-out blocks without an explanatory `// TODO:` or `// FIXME:` tag, and obsolete assets MUST be deleted before approval. Roslyn analyzers and IDE warnings MUST be addressed — zero suppression without a documented justification. Rationale: entropy compounds in long-lived projects; enforced hygiene prevents the accumulation of debt that makes onboarding and refactoring costly.

### II. SOLID & GoF Design (NON-NEGOTIABLE)
All production code MUST respect SOLID principles:
- **SRP**: Each class has one reason to change.
- **OCP**: Extend via abstraction, not modification.
- **LSP**: Derived types are substitutable for their base types.
- **ISP**: Prefer narrow, role-specific interfaces.
- **DIP**: Depend on abstractions; concrete types are wired by the DI container only.

Gang of Four (GoF) patterns SHOULD be applied wherever they reduce coupling or clarify intent and MUST be accompanied by a short comment naming the pattern and its purpose (e.g., `// Strategy pattern — swaps fee calculation algorithm per provider`). Rationale: pattern-named code is reviewable, onboardable, and evolvable without regression.

### III. Test Coverage for Every Major Feature (NON-NEGOTIABLE)
Every major feature MUST ship with all three test layers before the PR is merged:
- **Unit tests** (xUnit + NSubstitute): isolated business logic; no I/O or external dependencies.
- **Integration tests** (xUnit + `WebApplicationFactory<T>` + Testcontainers): full vertical-slice behaviour exercised in-process against a real (containerised) database.
- **E2E tests** (Playwright .NET): critical user journeys executed against a deployed or locally-hosted environment.

Red-Green-Refactor is the enforced TDD cycle. Tests written and approved by the user MUST fail before implementation begins. Rationale: multi-layer tests catch regressions at the layer where they are cheapest to fix; untested features are unfinished features.

### IV. Vertical Slice Architecture (VSA)
Features MUST be implemented as self-contained vertical slices. Each slice owns its:
- `Request` / `Response` DTOs
- FluentValidation `Validator`
- MediatR `Command` / `Query` + `Handler`
- Minimal API endpoint registration (`.MapGroup(...)`)
- Feature-specific extension methods or mappers

Shared infrastructure (auth middleware, error handling, logging, EF Core `DbContext`, shared value objects) lives in `src/Shared/` or `src/Infrastructure/` and MUST NOT be duplicated across slices. No horizontal root-level `Services/`, `Repositories/`, or `DTOs/` folders are permitted for feature-specific code. Rationale: VSA maximises cohesion within a feature and minimises coupling across features, enabling parallel development and safe deletion.

### V. Fixed Technology Stack (NON-NEGOTIABLE)
The stack is locked:
- **Front-end**: Blazor WebAssembly, .NET 10 — Radzen Blazor component library (`Radzen.Blazor`) MUST be used for any UI element beyond a basic HTML control (grids, charts, dialogs, date-pickers, autocompletes, etc.).
- **Back-end**: ASP.NET Core Minimal API, .NET 10 — MediatR for in-process request dispatching; FluentValidation for input validation; EF Core (Code-First migrations) for persistence.
- **Background scheduling**: Hangfire — used as the recursive job scheduler for the monitoring engine. Rationale: `IHostedService` alone has no built-in job persistence, retry semantics, or a built-in dashboard; Hangfire provides all three, which are required for a per-user per-route polling engine. _Amendment v1.1.0 — 2026-02-19._
- **Logging**: Serilog, wired as the sole `Microsoft.Extensions.Logging` backend via `AddSerilog()`. All application code depends on `ILogger<T>` only — no direct Serilog API references outside of bootstrap. Rationale: Serilog provides structured sink routing (including the WASM client log-forwarding endpoint) that the default MEL console sink does not support. _Amendment v1.1.0 — 2026-02-19._
- **Hosting**: A single Azure App Service hosts both the API and the Blazor WASM static files (published to the API project's `wwwroot`).
- **Auth**: ASP.NET Core Identity + JWT bearer tokens (or Azure Entra ID for enterprise scenarios — decision in `@sddp.plan`).
- **Observability**: OpenTelemetry SDK with Azure Monitor exporter; structured logging via `Microsoft.Extensions.Logging`.

No alternative UI libraries, ORMs, or hosting platforms may be introduced without a documented amendment. Rationale: a locked stack prevents dependency sprawl and ensures a shared mental model across the team.

## Technology Constraints

| Concern | Decision |
|---|---|
| Runtime | .NET 10 (LTS) throughout — no down-level TFMs |
| Hosting | Azure App Service — Blazor WASM static files served from API `wwwroot` |
| UI library | Radzen Blazor for all advanced UI controls |
| Architecture pattern | Vertical Slice via MediatR (CQRS-style) |
| Database access | EF Core (Code-First) — raw SQL only for performance-critical read projections |
| Validation | FluentValidation — all commands/queries validated before handler execution |
| Testing | xUnit, NSubstitute, Playwright .NET (C#), Testcontainers |
| Commits | Conventional Commits (`feat:`, `fix:`, `refactor:`, `test:`, `docs:`, `chore:`) |
| Branching | `main` is always deployable; features on `#####-feature-name` branches |

## Development Workflow

- **PR gate**: All CI checks (build + all test layers) MUST pass; at least one peer review required.
- **Naming conventions**: PascalCase for C# types and members; camelCase for JS/TS interop; kebab-case for API routes.
- **Zero-waste gate**: Reviewers MUST flag any dead code, unused dependency, or obsolete asset introduced by a PR.
- **Pattern comments**: Non-trivial design choices MUST include an explanatory comment naming the pattern or rationale.
- **Feature branches**: Prefix with a zero-padded issue/ticket number (e.g., `00001-traffic-ingestion`).

## Governance

These project instructions supersede all other practices. Amendments require:
1. A documented rationale for the change.
2. A version bump following semantic versioning:
   - **MAJOR**: Backward-incompatible principle removal or redefinition.
   - **MINOR**: New principle or section added, or materially expanded.
   - **PATCH**: Clarifications, wording fixes, typo corrections.
3. A consistency propagation check — run `@sddp.init` to sync spec, plan, and task templates.

All PRs and code reviews MUST verify compliance with these principles. Complexity beyond what these principles permit MUST be justified in writing in the PR description. Use `AGENTS.md` for runtime development guidance.

**Version**: 1.1.0 | **Last Amended**: 2026-02-19
