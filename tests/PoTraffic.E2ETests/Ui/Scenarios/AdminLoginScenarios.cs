using FluentAssertions;
using Microsoft.Playwright;
using PoTraffic.E2ETests.Ui.Helpers;

namespace PoTraffic.E2ETests.Ui.Scenarios;

/// <summary>
/// E2E scenario: seeds an admin account via the API, obtains a JWT via the testing-only
/// /e2e/dev-login endpoint (the email/password login form was removed in favour of
/// Microsoft OAuth), establishes a BFF cookie session, and verifies the dashboard loads.
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

    [SkipUnlessE2EReady]
    public async Task Admin_Login_Redirects_To_Dashboard()
    {
        // Arrange — seed a known admin user and obtain a JWT via the dev-login endpoint.
        using HttpClient apiHttp = new() { BaseAddress = new Uri(ApiBaseUrl) };
        TestingApiClient api = new(apiHttp);

        string email = await api.SeedAdminAsync();

        // Act — establish the cookie session in the browser, then open the dashboard.
        await AuthenticateViaDevLoginAsync(email);

        // Assert — should land on /dashboard and render its heading.
        await Page.WaitForURLAsync("**/dashboard", new() { Timeout = 30_000 });

        // The status bar this used to wait on now renders only when the app is offline or
        // stale, so it never appears on a healthy dashboard. The <h1> always does.
        ILocator heading = Page.Locator("h1.pt-page-title");
        await heading.WaitForAsync(new() { Timeout = 15_000 });
        (await heading.IsVisibleAsync()).Should().BeTrue("the dashboard heading should be visible after login");

        Page.Url.Should().Contain("/dashboard");
    }
}
