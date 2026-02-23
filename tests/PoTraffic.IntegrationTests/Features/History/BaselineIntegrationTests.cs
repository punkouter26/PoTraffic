namespace PoTraffic.IntegrationTests.Features.History;

/// <summary>
/// Integration tests for GET /api/routes/{id}/baseline.
///
/// These tests require a running Docker daemon and the azure-sql-edge container image.
/// Run with: dotnet test --filter "Category=Integration"
/// </summary>
public sealed class BaselineIntegrationTests : BaseIntegrationTest
{
    [Fact(Skip = "Requires Docker — run manually with TESTCONTAINERS_STARTUP_TIMEOUT=120")]
    public async Task GetBaseline_With3PlusSessions_ReturnsNonNullStdDev()
    {
        // Arrange
        HttpClient client = CreateClient();

        // TODO:
        // 1. Seed ≥3 distinct working days of PollRecord data for a route
        // 2. Authenticate via /e2e/dev-login
        // 3. GET /api/routes/{id}/baseline?dayOfWeek=Monday
        // 4. Assert response BaselineSlotDto has non-null StdDevDurationSeconds
        //    and MeanDurationSeconds for at least one slot

        await Task.CompletedTask;
    }

    [Fact(Skip = "Requires Docker — run manually with TESTCONTAINERS_STARTUP_TIMEOUT=120")]
    public async Task GetBaseline_WithFewerThan3Sessions_ReturnsEmptyArray()
    {
        // Arrange
        HttpClient client = CreateClient();

        // TODO:
        // 1. Seed <3 sessions for the route
        // 2. GET /api/routes/{id}/baseline?dayOfWeek=Monday
        // 3. Assert response has empty Slots array (HasSufficientData = false)

        await Task.CompletedTask;
    }
}
