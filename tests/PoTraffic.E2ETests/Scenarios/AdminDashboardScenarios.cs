// E2E test — requires live environment + Playwright .NET.
// Skipped in MVP: see tasks.md T089.
// US5: Admin logs in, navigates to /admin, verifies usage table present.
namespace PoTraffic.E2ETests.Scenarios;

public sealed class AdminDashboardScenarios
{
    // TODO: T089 — implement using Playwright .NET + TestingApiClient.DevLoginAsync("Administrator")
    // Critical path: /e2e/dev-login?role=Administrator → /admin → usage table visible
}
