# AGENTS.md

Contract for coding agents working in this repository. Architecture, commands, and code
conventions live in [CLAUDE.md](CLAUDE.md); this file covers how work is delivered.

## Getting oriented

If a `DOCS/` folder exists at the repo root, read it first for an overall summary of the
project, then [CLAUDE.md](CLAUDE.md) for architecture and commands.

## Branching

**Work directly on `master`. Do not create branches unless explicitly asked.**

No feature branches, no `design/*` branches, no worktrees — commit to `master`. If a change
seems large enough to want isolation, ask first rather than branching on your own initiative.

Why: branches accumulated here faster than they were merged, so `master` stopped being the
latest code and reconciling them later cost more than the isolation was worth.

Corollary: if a branch does exist and is finished, merge it into `master` rather than
leaving it open.

## Configuration and secrets

Do not use `dotnet user-secrets` for local data. Put settings in `appsettings*.json`, and
secrets in Azure Key Vault (`KeyVault:Uri`) when one exists.

## Verifying a change

1. **Build clean.** `TreatWarningsAsErrors` is on — a compiler warning is a build break, so
   fix warnings rather than suppress them.
2. **Restart the app and confirm it came back.** After any code change, restart the dev
   host (`./SCRIPTS/start-dev.ps1`) and check it is serving (`/health/json` returns 200).
3. **Test only what you touched.** Do not run the whole suite after a change. Run the tests
   covering the changed code, or none at all when the change is simple. The full run
   (`pwsh ./SCRIPTS/run-tests.ps1`) is for the user to invoke deliberately.
4. **UI changes get a before/after screenshot**, annotated to point out what changed.

Keep test files themselves correct and up to date when a change invalidates them.

Do it for the user: if a step can be done with a command or tool, run it rather than asking
the user to type commands or click through a web portal.

### Running a subset of tests

```powershell
dotnet test tests/PoTraffic.UnitTests --filter "FullyQualifiedName~CreateRouteValidator"
dotnet test tests/PoTraffic.E2ETests --filter "FullyQualifiedName~MonitoringWindowScenarios"
```

E2E filters need a Testing host on `E2E_BASE_URL` (default `http://localhost:5150`); start it
with `dotnet run --project src/PoTraffic.API --launch-profile Testing` and leave it up across
iterations rather than letting the script restart it every time.

When fixing a batch of failures, never re-run the whole suite after each fix — run the tests
you are working on, and the full suite once at the end.

## Committing and pushing

`.github/workflows/deploy.yml` deploys to the production App Service on every push to
`master` — a push is a deploy, not a save. Do not push unless asked.

When asked to sync (commit and push), write a short, casual commit message in plain
American slang — not technical jargon — so it reads like a person wrote it, then push.

## Reporting back

- If a request removed more than 100 lines of code overall, say so.
- If an answer runs longer than 100 words, end it with a TL;DR of about 20 words.
