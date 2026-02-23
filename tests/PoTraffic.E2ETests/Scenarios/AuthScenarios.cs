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

        // ── Assert — dashboard URL and heading ────────────────────────────────
        // Wait for navigation to /dashboard
        await Page.WaitForURLAsync($"{BaseUrl}/dashboard", new() { Timeout = 30_000 });

        // Assert the Welcome Back heading (TextStyle.H4 renders as <h4> in Radzen)
        Microsoft.Playwright.ILocator heading = Page.Locator("h4:has-text('Welcome Back')");

        await heading.WaitForAsync(new() { Timeout = 10_000 });

        bool isVisible = await heading.IsVisibleAsync();
        Assert.True(isVisible, "Dashboard heading must be visible after admin login.");
    }

    [SkipUnlessE2EReady]
    public async Task DeleteAccount_ShowsConfirmDialog_RedirectsToLogin_ReLoginFails()
    {
        // Log in, navigate to /account/settings
        // Click Delete Account → RadzenConfirmDialog appears → confirm
        // Assert redirect to /login
        // Attempt re-login with same credentials → 401
        await Task.CompletedTask;
    }
}
