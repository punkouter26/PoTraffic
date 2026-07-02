using System.Net.Http.Json;
using System.Text.RegularExpressions;
using PoTraffic.E2ETests.Helpers;

namespace PoTraffic.E2ETests.Scenarios;

/// <summary>
/// E2E scenarios for authentication flows.
/// Tests execute against the running app at E2E_BASE_URL (default: http://localhost:5150).
/// The [SkipUnlessE2EReady] attribute auto-skips when Playwright binaries or the app
/// are not available, so CI passes without a live environment.
///
/// The email/password login form was removed — Microsoft OAuth is the only normal
/// sign-in path. Tests that need an authenticated session establish a BFF cookie
/// session via the testing-only /e2e/dev-login endpoint from within the browser.
/// </summary>
public sealed class AuthScenarios : PlaywrightTestBase
{

    /// <summary>
    /// Verifies that an Administrator user lands on the /dashboard with the status bar visible
    /// after authenticating via the dev-login endpoint.
    ///
    /// Pre-conditions:
    ///   - App running at E2E_BASE_URL (http://localhost:5150 by default)
    ///   - Playwright Chromium binaries installed
    ///   - /e2e/seed-admin and /e2e/dev-login endpoints reachable (Testing environment)
    /// </summary>
    [SkipUnlessE2EReady]
    public async Task AdminUser_Login_LandsDashboardWithHeading()
    {
        // ── Arrange — seed a known admin user and obtain a JWT via dev-login ────
        using HttpClient apiHttp = new() { BaseAddress = new Uri(BaseUrl) };
        TestingApiClient api = new(apiHttp);

        string email = await api.SeedAdminAsync();

        // Capture browser console messages for diagnosing WASM/JS failures
        var consoleMessages = new System.Collections.Generic.List<string>();
        Page.Console += (_, msg) => consoleMessages.Add($"[{msg.Type}] {msg.Text}");
        Page.PageError += (_, err) => consoleMessages.Add($"[PAGE ERROR] {err}");

        // ── Act — establish the cookie session in the browser, then open the dashboard ──
        await AuthenticateViaDevLoginAsync(email);

        // ── Assert — dashboard URL and status bar ──────────────────────────────
        try
        {
            await Page.WaitForURLAsync($"{BaseUrl}/dashboard", new() { Timeout = 30_000 });
        }
        catch (Exception ex)
        {
            string diagnostics = string.Join("\n", consoleMessages.TakeLast(30));
            throw new InvalidOperationException(
                $"Dashboard did not load within 30s.\n" +
                $"Current URL: {Page.Url}\n" +
                $"Browser console (last 30):\n{diagnostics}", ex);
        }

        // The DashboardPage shows a pt-status-bar with route counts and an "+ Add Route" button.
        Microsoft.Playwright.ILocator statusBar = Page.Locator(".pt-status-bar");
        await statusBar.WaitForAsync(new() { Timeout = 15_000 });

        bool isVisible = await statusBar.IsVisibleAsync();
        Assert.True(isVisible, "Dashboard status bar must be visible after admin login.");

        Microsoft.Playwright.ILocator mockDataBadge = Page.Locator(".pt-mock-data-badge");
        await mockDataBadge.WaitForAsync(new() { Timeout = 15_000 });
        Assert.Equal("USING MOCK DATA", await mockDataBadge.InnerTextAsync());
    }

    [SkipUnlessE2EReady]
    public async Task GuestLogin_BypassesOAuth_EstablishesCookieSession_AndDisplaysGuestId()
    {
        // Guest login must happen inside the browser so the HttpOnly session
        // cookie lands in the Playwright context.
        await Page.GotoAsync($"{BaseUrl}/login");
        await Page.EvaluateAsync(
            @"async () => {
                const resp = await fetch('/api/auth/guest-login', { method: 'POST' });
                if (!resp.ok) throw new Error('guest-login failed: ' + resp.status);
            }");

        await Page.GotoAsync($"{BaseUrl}/dashboard");

        Microsoft.Playwright.ILocator userChip = Page.Locator(".pt-user-chip");
        await userChip.WaitForAsync(new() { Timeout = 30_000 });
        string chipText = await userChip.InnerTextAsync();

        Assert.Matches(new Regex(@"GUEST\d{8}\s+LOGGED IN", RegexOptions.IgnoreCase), chipText);
    }

    [SkipUnlessE2EReady]
    public async Task LoginPage_ShowsMicrosoftButton()
    {
        // ── Pre-condition — skip if no OAuth providers configured in this env ────
        using HttpClient apiHttp = new() { BaseAddress = new Uri(BaseUrl) };
        var providersResponse = await apiHttp.GetFromJsonAsync<ProvidersDto>("/api/auth/providers");
        bool hasMicrosoft = providersResponse?.Providers.Contains("microsoft", StringComparer.OrdinalIgnoreCase) ?? false;

        if (!hasMicrosoft)
        {
            // Microsoft OAuth is not configured in this environment (Testing env has no OAuth keys).
            // The button is conditionally rendered via @if (_availableProviders.Contains(...)),
            // so this test is only meaningful when the provider is active.
            return;
        }

        await Page.GotoAsync($"{BaseUrl}/login");

        // RadzenButton Text includes an emoji prefix (e.g. "  Continue with Microsoft").
        // Use Exact = false for partial accessible-name matching.
        var microsoftBtn = Page.GetByRole(Microsoft.Playwright.AriaRole.Button,
            new() { Name = "Continue with Microsoft", Exact = false });
        await microsoftBtn.WaitForAsync(new() { Timeout = 30_000 });
        Assert.True(await microsoftBtn.IsVisibleAsync(), "Microsoft sign-in button must be visible on login page.");
    }

    private sealed record ProvidersDto(List<string> Providers);
}
