using FluentAssertions;
using Microsoft.Playwright;
using PoTraffic.E2ETests.Helpers;

namespace PoTraffic.E2ETests.Scenarios;

/// <summary>
/// E2E scenario: seeds an admin account via the API, obtains a JWT via the testing-only
/// /e2e/dev-login endpoint (the email/password login form was removed in favour of
/// Microsoft OAuth), injects it into localStorage, and verifies the dashboard loads.
/// </summary>
public sealed class AdminLoginScenarios : PlaywrightTestBase
{
    /// <summary>
    /// API base URL for seeding test data.
    /// Reads from E2E_API_URL environment variable; defaults to E2E_BASE_URL or http://localhost:5150.
    /// </summary>
    private static string ApiBaseUrl =>
        Environment.GetEnvironmentVariable("E2E_API_URL")
        ?? Environment.GetEnvironmentVariable("E2E_BASE_URL")
        ?? "http://localhost:5150";

    private const string LocalStorageTokenKey = "potraffic_access_token";

    [SkipUnlessE2EReady]
    public async Task Admin_Login_Redirects_To_Dashboard()
    {
        // Arrange — seed a known admin user and obtain a JWT via the dev-login endpoint.
        using HttpClient apiHttp = new() { BaseAddress = new Uri(ApiBaseUrl) };
        TestingApiClient api = new(apiHttp);

        (string email, _) = await api.SeedAdminAsync();
        string? token = await api.DevLoginAsync(email, role: "Administrator");
        token.Should().NotBeNullOrWhiteSpace("dev-login must issue an Administrator JWT");

        // Inject the JWT into localStorage so the WASM app treats us as authenticated.
        await Page.GotoAsync("/login");
        await Page.EvaluateAsync(
            "([key, value]) => localStorage.setItem(key, value)",
            new[] { LocalStorageTokenKey, token! });

        // Act — navigate to the dashboard with the token already present.
        await Page.GotoAsync("/dashboard");

        // Assert — should land on /dashboard and show the status bar.
        await Page.WaitForURLAsync("**/dashboard", new() { Timeout = 30_000 });

        // Verify the pt-status-bar is visible — DashboardPage always renders this for authenticated users.
        ILocator statusBar = Page.Locator(".pt-status-bar");
        await statusBar.WaitForAsync(new() { Timeout = 15_000 });
        (await statusBar.IsVisibleAsync()).Should().BeTrue("the dashboard status bar should be visible after login");

        Page.Url.Should().Contain("/dashboard");
    }
}
