# Agent Skills (Rule 12)

> The Po* coding standards require these awesome-copilot skills in the agent's
> global config. Install them once in your editor / agent host so they are
> available alongside the Po* conventions.

## Phase 1 — Understand the Codebase (Day 1)

### 1. `acquire-codebase-knowledge`
Map everything before touching a line. Generates `STACK`, `ARCHITECTURE`,
`CONVENTIONS`, `TESTING` docs. Skip if you're starting from zero.

## Phase 2 — Design & Plan (Before Writing Code)

### 2. `architecture-blueprint-generator`
Define your layers, patterns, and component boundaries. Becomes the blueprint
every future decision references.

### 3. `folder-structure-blueprint-generator`
Translate the architecture into a concrete folder layout. Establishes where
every file type lives before you create any.

## Phase 3 — Build Features (Daily Development)

### 5. `dotnet-best-practices`
Apply while writing any C# class — DI, async, error handling, configuration
patterns.

### 6. `dotnet-design-pattern-review`
Review code after writing a service or domain object. Catches pattern violations
before they compound.

### 7. `autoresearch`
Use when you need to iteratively optimize something measurable — test pass
rate, response time, build size. Runs a full experiment loop autonomously.

## Phase 6 — Harden & Secure (Before PR / Merge)

### 10. `security-review`
Full OWASP scan before merging to main. Catches secrets, data-flow
vulnerabilities, and injection risks across the whole changeset.

## Phase 7 — Observability (Before Deploying to Azure)

### 11. `appinsights-instrumentation`
Wire up telemetry before you go live. Useless to add after an incident — you
want data from day one.

## Phase 8 — Deploy

### 12. `azure-deployment-preflight`
Run immediately before `azd up`. Validates Bicep syntax, previews what-if
changes, checks permissions. Saves costly rollbacks.

## Phase 9 — Operate (Post-Deployment)

### 13. `azure-resource-health-diagnose`
Triggered reactively when something breaks in Azure, or proactively on a
schedule. Queries logs, classifies issues, generates a remediation plan.

## Phase 10 — Document (End of Milestone / Sprint)

### 14. `create-readme`
Write the README once the project is stable enough to describe accurately.

### 16. `repo-story-time`
End of a release or major milestone. Mines git history and generates
`REPOSITORY_SUMMARY.md` + a narrative story of the project's evolution.

---

## Local Substitute Docs

If a skill is unavailable in your agent host, the equivalent local
documentation lives under `docs/`:

| Skill | Local Substitute |
|---|---|
| `acquire-codebase-knowledge` | `docs/Architecture.mmd`, `docs/ComponentMap.mmd`, `docs/DataModel.mmd` |
| `dotnet-best-practices` | `AGENTS.md` § 2, § 3, § 7 |
| `dotnet-design-pattern-review` | XML doc comments on GoF/SOLID patterns (Rule 2) |
| `appinsights-instrumentation` | `docs/AzureDeployment.md` § Observability |
| `azure-deployment-preflight` | `docs/DevOps.md`, `docs/AzureDeployment.md` |
| `azure-resource-health-diagnose` | `docs/AzureDeployment.md` § Troubleshooting |
| `create-readme` | `README.md` |
| `repo-story-time` | (none — generate on demand) |
