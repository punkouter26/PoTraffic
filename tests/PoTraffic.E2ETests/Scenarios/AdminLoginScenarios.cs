using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.Playwright;
using PoTraffic.E2ETests.Helpers;

namespace PoTraffic.E2ETests.Scenarios;

/// <summary>
/// E2E scenario: registers an admin account via the API, then logs in through the
/// Blazor WASM UI and verifies the dashboard page loads successfully.
/// </summary>
public sealed class AdminLoginScenarios : PlaywrightTestBase
{
    /// <summary>
    /// API base URL for seeding test data (account registration).
    /// Reads from E2E_API_URL environment variable; defaults to http://localhost:5150.
    /// </summary>
    private static string ApiBaseUrl =>
        Environment.GetEnvironmentVariable("E2E_API_URL") ?? "http://localhost:5150";

    private const string TestEmail = "admin@potraffic.dev";
    private const string TestPassword = "Admin123!";

    [SkipUnlessE2EReady]
    public async Task Admin_Login_Redirects_To_Dashboard()
    {
        // Arrange — ensure the test account exists by calling the register endpoint directly.
        // A 409 Conflict (already registered) is acceptable; any other non-success code is a failure.
        using HttpClient apiClient = new() { BaseAddress = new Uri(ApiBaseUrl) };
        HttpResponseMessage registerResponse = await apiClient.PostAsJsonAsync(
            "/api/auth/register",
            new { Email = TestEmail, Password = TestPassword, Locale = "Europe/London" });

        registerResponse.StatusCode.Should().BeOneOf(
            System.Net.HttpStatusCode.OK,
            System.Net.HttpStatusCode.Created,
            System.Net.HttpStatusCode.Conflict,
            System.Net.HttpStatusCode.NoContent);

        // Act — navigate to the login page
        await Page.GotoAsync("/login");

        // Clear pre-filled values and type test credentials
        // RadzenTextBox renders as input.rz-textbox (type="text") regardless of InputAttributes;
        // searching by type="email" or name="email" does not match the Radzen-rendered input.
        // 90 s covers .NET WASM cold-start in headless CI.
        ILocator emailInput = Page.Locator("input.rz-textbox").First;
        await emailInput.WaitForAsync(new() { Timeout = 90_000 });
        ILocator passwordInput = Page.Locator("input[type='password']").First;

        await emailInput.ClearAsync();
        await emailInput.FillAsync(TestEmail);

        await passwordInput.ClearAsync();
        await passwordInput.FillAsync(TestPassword);

        // Click the Sign In button
        await Page.GetByRole(AriaRole.Button, new() { Name = "Sign In" }).ClickAsync();

        // Assert — should navigate to /dashboard and show the Dashboard heading
        await Page.WaitForURLAsync("**/dashboard", new() { Timeout = 15_000 });

        // Verify the page contains the Welcome Back heading (H4)
        ILocator dashboardHeading = Page.GetByRole(AriaRole.Heading, new() { Name = "Welcome Back" });
        await dashboardHeading.WaitForAsync(new() { Timeout = 10_000 });
        (await dashboardHeading.IsVisibleAsync()).Should().BeTrue("the Welcome Back heading should be visible after login");

        // Verify the URL is correct
        Page.Url.Should().Contain("/dashboard");

        // Verify the nav menu shows authenticated links (Dashboard, Routes, Settings)
        ILocator navDashboard = Page.Locator(".sidebar").GetByText("Dashboard");
        (await navDashboard.IsVisibleAsync()).Should().BeTrue("the Dashboard nav link should appear for authenticated users");

        ILocator navRoutes = Page.Locator(".sidebar").GetByText("Routes");
        (await navRoutes.IsVisibleAsync()).Should().BeTrue("the Routes nav link should appear for authenticated users");
    }
}
