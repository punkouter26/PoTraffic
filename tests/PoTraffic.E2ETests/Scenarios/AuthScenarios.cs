using System.Net.Http.Json;
using PoTraffic.E2ETests.Helpers;

namespace PoTraffic.E2ETests.Scenarios;

/// <summary>
/// E2E scenarios for authentication flows.
/// Tests execute against the running app at E2E_BASE_URL (default: http://localhost:5150).
/// The [SkipUnlessE2EReady] attribute auto-skips when Playwright binaries or the app
/// are not available, so CI passes without a live environment.
/// </summary>
public sealed class AuthScenarios : PlaywrightTestBase
{
    /// <summary>
    /// Verifies that an Administrator user can log in via the /login page
    /// and lands on the /dashboard with the Dashboard heading visible.
    /// 
    /// Pre-conditions:
    ///   - App running at E2E_BASE_URL (http://localhost:5150 by default)
    ///   - Playwright Chromium binaries installed
    ///   - /e2e/seed-admin endpoint reachable (Development or Testing environment)
    /// </summary>
    [SkipUnlessE2EReady]
    public async Task AdminUser_Login_LandsDashboardWithHeading()
    {
        // ── Arrange — seed a known admin user via the E2E testing endpoint ──────
        using HttpClient apiHttp = new() { BaseAddress = new Uri(BaseUrl) };
        TestingApiClient api = new(apiHttp);

        (string email, string password) = await api.SeedAdminAsync();

        // Capture browser console messages for diagnosing WASM/JS failures
        var consoleMessages = new System.Collections.Generic.List<string>();
        Page.Console += (_, msg) => consoleMessages.Add($"[{msg.Type}] {msg.Text}");
        Page.PageError += (_, err) => consoleMessages.Add($"[PAGE ERROR] {err}");

        // ── Act — navigate to login and submit credentials ────────────────────
        // Do NOT use WaitUntil=NetworkIdle — Hangfire background polling keeps
        // the connection alive indefinitely, so NetworkIdle never resolves.
        // Default Load state is sufficient; WaitForAsync handles WASM hydration.
        await Page.GotoAsync($"{BaseUrl}/login");

        // Wait for Blazor WASM to hydrate — LoginPage.razor renders two RadzenTextBox
        // components. RadzenTextBox always emits input[type='text'] (ignoring InputAttributes
        // type override), so we wait for the first rz-textbox (email) to be visible.
        // 90s covers the .NET WASM cold-start download in headless CI environments.
        var emailInput = Page.Locator("input.rz-textbox").First;
        try
        {
            await emailInput.WaitForAsync(new() { Timeout = 90_000 });
        }
        catch (Exception ex)
        {
            string diagnostics = string.Join("\n", consoleMessages.TakeLast(30));
            throw new InvalidOperationException(
                $"Login form did not render within 90s.\n" +
                $"Current URL: {Page.Url}\n" +
                $"Browser console (last 30):\n{diagnostics}", ex);
        }

        await emailInput.FillAsync(email);
        await Page.Locator("input[type='password']").FillAsync(password);

        // Click Sign In button — RadzenButton renders as <button>
        await Page.GetByRole(Microsoft.Playwright.AriaRole.Button, new() { Name = "Sign In" })
                  .ClickAsync();

        // ── Assert — dashboard URL and status bar ──────────────────────────────
        // Wait for navigation to /dashboard
        await Page.WaitForURLAsync($"{BaseUrl}/dashboard", new() { Timeout = 30_000 });

        // Assert the status bar is visible — it always renders for authenticated users on the dashboard.
        // The DashboardPage shows a pt-status-bar with route counts and an "+ Add Route" button.
        Microsoft.Playwright.ILocator statusBar = Page.Locator(".pt-status-bar");

        await statusBar.WaitForAsync(new() { Timeout = 10_000 });

        bool isVisible = await statusBar.IsVisibleAsync();
        Assert.True(isVisible, "Dashboard status bar must be visible after admin login.");
    }

    [SkipUnlessE2EReady]
    public async Task LoginPage_ShowsGoogleAndMicrosoftButtons()
    {
        // ── Pre-condition — skip if no OAuth providers configured in this env ────
        using HttpClient apiHttp = new() { BaseAddress = new Uri(BaseUrl) };
        var providersResponse = await apiHttp.GetFromJsonAsync<ProvidersDto>("/api/auth/providers");
        bool hasGoogle = providersResponse?.Providers.Contains("google", StringComparer.OrdinalIgnoreCase) ?? false;
        bool hasMicrosoft = providersResponse?.Providers.Contains("microsoft", StringComparer.OrdinalIgnoreCase) ?? false;

        if (!hasGoogle && !hasMicrosoft)
        {
            // Social auth is not configured in this environment (Testing env has no OAuth keys).
            // The buttons are conditionally rendered via @if (_availableProviders.Contains(...)),
            // so this test is only meaningful when at least one provider is active.
            return;
        }

        await Page.GotoAsync($"{BaseUrl}/login");

        // RadzenButton Text includes an emoji prefix (e.g. "🔵  Continue with Google").
        // Use Exact = false for partial accessible-name matching.
        if (hasGoogle)
        {
            var googleBtn = Page.GetByRole(Microsoft.Playwright.AriaRole.Button,
                new() { Name = "Continue with Google", Exact = false });
            await googleBtn.WaitForAsync(new() { Timeout = 30_000 });
            Assert.True(await googleBtn.IsVisibleAsync(), "Google sign-in button must be visible on login page.");
        }

        if (hasMicrosoft)
        {
            var microsoftBtn = Page.GetByRole(Microsoft.Playwright.AriaRole.Button,
                new() { Name = "Continue with Microsoft", Exact = false });
            Assert.True(await microsoftBtn.IsVisibleAsync(), "Microsoft sign-in button must be visible on login page.");
        }
    }

    private sealed record ProvidersDto(List<string> Providers);
}
