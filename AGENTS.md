# AGENTS.md

Contract for coding agents working in this repository. Architecture, commands, and code
conventions live in [CLAUDE.md](CLAUDE.md); this file covers how work is delivered.

## Branching

**Work directly on `master`. Do not create branches unless explicitly asked.**

No feature branches, no `design/*` branches, no worktrees — commit to `master`. If a change
seems large enough to want isolation, ask first rather than branching on your own initiative.

Why: branches accumulated here faster than they were merged, so `master` stopped being the
latest code and reconciling them later cost more than the isolation was worth.

Corollary: if a branch does exist and is finished, merge it into `master` rather than
leaving it open.

## Verifying a change

**Do not run the test suites after changing code.** Build (`dotnet build`) to prove it
compiles, and stop there. Tests are run deliberately, by the user, via
`pwsh ./SCRIPTS/run-tests.ps1` — they are not part of the edit loop.

Keep test files themselves correct and up to date when a change invalidates them; just do
not execute the suites to check.

### When you have been asked to run tests

**Never re-run the whole suite after each fix.** A full run is minutes of waiting — the E2E
tiers alone spin up a Testing host and drive a browser — so a fix-then-rerun-everything loop
burns most of its time re-proving code nobody touched.

Run the full suite once to find out what is broken. From then on, run only the tests you are
actually working on:

```powershell
dotnet test tests/PoTraffic.UnitTests --filter "FullyQualifiedName~CreateRouteValidator"
dotnet test tests/PoTraffic.E2ETests --filter "FullyQualifiedName~MonitoringWindowScenarios"
```

E2E filters need a Testing host on `E2E_BASE_URL` (default `http://localhost:5150`); start it
with `dotnet run --project src/PoTraffic.API --launch-profile Testing` and leave it up across
iterations rather than letting the script restart it every time.

Re-run the full suite once at the end, to confirm the fixes hold together — not between them.

## Pushing

Do not `git push` without being asked. `.github/workflows/deploy.yml` deploys to the
production App Service on every push to `master` — a push is a deploy, not a save.
