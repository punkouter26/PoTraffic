namespace PoTraffic.E2ETests.Scenarios;

/// <summary>
/// E2E scenario: Dashboard renders dual-series Radzen chart with historical baseline and live data.
///
/// Prerequisites:
///   - API + Blazor WASM running locally with ASPNETCORE_ENVIRONMENT=Testing
///
/// Run with: dotnet test --filter "Category=E2E"
/// </summary>
public sealed class DashboardScenarios
{
    [SkipUnlessE2EReady]
    public async Task DashboardPage_WithHistoricalAndCurrentData_RendersRadzenChart()
    {
        // Arrange
        // TODO:
        // 1. POST /e2e/seed to create route with ≥3 sessions of historical poll data + active session
        // 2. POST /e2e/dev-login to get JWT
        // 3. Navigate to /dashboard in Playwright browser
        // 4. Click on the route card / row to navigate to RouteDetailPage
        // 5. Assert two chart series are visible (Today's Actual + Historical Baseline)
        // 6. Assert delta shading band is rendered
        // 7. Assert reroute tooltip appears when hovering a rerouted record

        await Task.CompletedTask;
    }
}
