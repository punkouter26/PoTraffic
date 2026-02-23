namespace PoTraffic.IntegrationTests.Features.MonitoringWindows;

/// <summary>
/// Integration tests for POST /api/routes/{id}/windows/{wid}/start with quota exhausted.
///
/// These tests require a running Docker daemon and the azure-sql-edge container image.
/// Run with: dotnet test --filter "Category=Integration"
/// </summary>
public sealed class StartWindowIntegrationTests : BaseIntegrationTest
{
    [Fact(Skip = "Requires Docker — run manually with TESTCONTAINERS_STARTUP_TIMEOUT=120")]
    public async Task StartWindow_WhenQuotaExhausted_Returns429AndNoNewSession()
    {
        // Arrange
        HttpClient client = CreateClient();

        // TODO: using the /e2e/seed endpoint, create a user with 10 existing sessions for today,
        // then attempt to start a new window.
        //
        // Expected:
        //   POST /api/routes/{id}/windows/{wid}/start → 429 Too Many Requests
        //   No new MonitoringSession row in DB for that user today
        //
        // Example (skeleton):
        // await client.PostAsJsonAsync("/e2e/seed", seedPayload);
        // var loginResp = await client.PostAsJsonAsync("/e2e/dev-login", new { userId });
        // var token = await loginResp.Content.ReadFromJsonAsync<TokenResponse>();
        // client.DefaultRequestHeaders.Authorization = new("Bearer", token.AccessToken);
        //
        // var startResp = await client.PostAsync($"/api/routes/{routeId}/windows/{windowId}/start", null);
        // startResp.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        //
        // using var scope = GetScope();
        // var db = scope.ServiceProvider.GetRequiredService<PoTrafficDbContext>();
        // var sessionCount = await db.MonitoringSessions.CountAsync(s => s.Route.UserId == userId ...);
        // sessionCount.Should().Be(QuotaConstants.DefaultDailyQuota);

        await Task.CompletedTask; // placeholder
    }
}
