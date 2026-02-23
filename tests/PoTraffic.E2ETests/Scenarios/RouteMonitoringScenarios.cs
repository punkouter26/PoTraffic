namespace PoTraffic.E2ETests.Scenarios;

/// <summary>
/// E2E scenario: full route monitoring flow via real browser.
///
/// Prerequisites:
///   - API + Blazor WASM running at the URL configured in playwright.config.json
///   - ASPNETCORE_ENVIRONMENT=Testing (enables /e2e/* endpoints)
///
/// Run with: dotnet test --filter "Category=E2E"
/// </summary>
public sealed class RouteMonitoringScenarios
{
    [SkipUnlessE2EReady]
    public async Task CreateRoute_StartMonitoring_AssertPollRecordsAppear()
    {
        // Arrange
        // TODO: Wire Playwright browser + page from IAsyncLifetime base class (T110).
        //
        // Steps:
        //   1. Call POST /e2e/seed to create a user + route + monitoring window in DB
        //   2. Call POST /e2e/dev-login to obtain a JWT for that user
        //   3. Navigate to /dashboard in the browser
        //   4. Find the route in the RadzenDataGrid, click "Start Monitoring"
        //   5. Wait up to 30s for at least one poll record to appear in poll history
        //   6. Assert the poll record row is visible in the Route Detail page
        //
        // Example (skeleton):
        // await using var playwright = await Playwright.CreateAsync();
        // await using var browser = await playwright.Chromium.LaunchAsync();
        // var page = await browser.NewPageAsync();
        //
        // await page.GotoAsync("http://localhost:5000");
        // await page.FillAsync("[data-testid='email']", "test@example.com");
        // await page.FillAsync("[data-testid='password']", "Test!1234");
        // await page.ClickAsync("[data-testid='login-btn']");
        // await page.WaitForURLAsync("**/dashboard");
        //
        // ... etc.

        await Task.CompletedTask; // placeholder
    }
}
