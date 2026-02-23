// E2E test — requires live environment + Playwright .NET.
// Skipped in MVP: see tasks.md T100.
// FR-031: User navigates to Settings, clicks "Delete Account", confirms — account removed.
namespace PoTraffic.E2ETests.Scenarios;

public sealed class DeleteAccountE2EScenarios
{
    // TODO: T100 — implement using Playwright .NET + TestingApiClient
    // Critical path: login → /account/settings → click Delete Account → confirm → redirected to /login
}
