using System.Text.RegularExpressions;
using PoTraffic.E2ETests.Ui.Helpers;

namespace PoTraffic.E2ETests.Ui.Scenarios;

/// <summary>
/// E2E scenarios for the Add Route user story.
///
/// Prerequisites:
///   - API + Blazor WASM running at E2E_BASE_URL (default: http://localhost:5150)
///   - Playwright Chromium binaries installed
///   - ASPNETCORE_ENVIRONMENT=Development or Testing (enables /e2e/* endpoints)
///
/// Run with: dotnet test tests/PoTraffic.E2EUI --filter "FullyQualifiedName~CreateRouteScenarios"
/// </summary>
public sealed class CreateRouteScenarios : PlaywrightTestBase
{
    private const string OriginAddress = "501 Sylview Dr, Pasadena, CA";
    private const string DestinationAddress = "456 S Fair Oaks Ave, Pasadena, CA";

    /// <summary>
    /// Verifies that a seeded route (origin + destination) appears in the Routes grid
    /// after an authenticated user navigates to /routes.
    ///
    /// Uses /e2e/seed-route to bypass the stubbed geocoding provider so the route can
    /// be persisted directly in the database. This isolates the UI assertion from the
    /// provider integration.
    /// </summary>
    [SkipUnlessE2EReady]
    public async Task AddRoute_WithOriginAndDestination_AppearsInRoutesList()
    {
        // ── Arrange ─────────────────────────────────────────────────────────────
        using HttpClient apiHttp = new() { BaseAddress = new Uri(BaseUrl) };
        TestingApiClient api = new(apiHttp);

        // Seed admin user + a route for that user
        string email = await api.SeedAdminAsync();
        Assert.NotNull(await api.DevLoginAsync(email, role: "Administrator"));

        (_, string origin, string destination) = await api.SeedRouteAsync(
            email, OriginAddress, DestinationAddress);

        var consoleMessages = new List<string>();
        Page.Console += (_, msg) => consoleMessages.Add($"[{msg.Type}] {msg.Text}");
        Page.PageError += (_, err) => consoleMessages.Add($"[PAGE ERROR] {err}");

        // ── Act — authenticate through the Testing-only token path ──────────────
        await AuthenticateViaDevLoginAsync(email);
        await Page.WaitForURLAsync($"{BaseUrl}/dashboard", new() { Timeout = 30_000 });

        // Go straight to /dashboard, where routes are shown as cards. There is no /routes
        // page and nothing redirects from it — the SPA fallback simply renders not-found,
        // so waiting for a redirect that no longer exists times out.
        await Page.GotoAsync($"{BaseUrl}/dashboard");

        // Wait for the dashboard heading (signals the page finished loading). The status bar
        // renders only when offline or stale, so it never signals a healthy load.
        await Page.Locator("h1.pt-page-title").WaitForAsync(new() { Timeout = 30_000 });

        // ── Assert — addresses visible in route cards (pt-route-addr-from / pt-route-addr-to) ──
        // /routes was merged into /dashboard (C1/UX-6). Routes render as .pt-route-card.
        string originStreet = origin.Split(',').First();
        Microsoft.Playwright.ILocator originAddr =
            Page.Locator(".pt-route-addr-from").Filter(new() { HasText = originStreet }).First;
        await originAddr.WaitForAsync(new() { Timeout = 15_000, State = Microsoft.Playwright.WaitForSelectorState.Visible });

        bool originVisible = await originAddr.IsVisibleAsync();
        Assert.True(originVisible,
            $"Expected origin street '{originStreet}' to be visible in a route card.");

        string destStreet = destination.Split(',').First();
        Microsoft.Playwright.ILocator destAddr =
            Page.Locator(".pt-route-addr-to").Filter(new() { HasText = destStreet }).First;
        await destAddr.WaitForAsync(new() { Timeout = 15_000, State = Microsoft.Playwright.WaitForSelectorState.Visible });

        bool destVisible = await destAddr.IsVisibleAsync();
        Assert.True(destVisible,
            $"Expected destination street '{destStreet}' to be visible in a route card.");
    }

    /// <summary>
    /// Verifies that clicking "Verify" on the Create Route form calls the correct
    /// API endpoint and surfaces a response in the UI (no crash, no unhandled 404).
    ///
    /// With the stub provider, geocoding returns GEOCODE_FAILED, so the expected
    /// UI text is "could not be verified". The assertion intentionally accepts both
    /// outcomes so that the test is green whether a real provider key is configured.
    /// </summary>
    [SkipUnlessE2EReady]
    public async Task CreateRouteForm_VerifyOriginAddress_ShowsResponseMessage()
    {
        // ── Arrange ─────────────────────────────────────────────────────────────
        using HttpClient apiHttp = new() { BaseAddress = new Uri(BaseUrl) };
        TestingApiClient api = new(apiHttp);
        string email = await api.SeedAdminAsync();
        Assert.NotNull(await api.DevLoginAsync(email, role: "Administrator"));

        var consoleMessages = new List<string>();
        Page.Console += (_, msg) => consoleMessages.Add($"[{msg.Type}] {msg.Text}");
        Page.PageError += (_, err) => consoleMessages.Add($"[PAGE ERROR] {err}");

        // ── Act — authenticate then navigate to create-route form ───────────────
        await AuthenticateViaDevLoginAsync(email);
        await Page.WaitForURLAsync($"{BaseUrl}/dashboard", new() { Timeout = 30_000 });

        await Page.GotoAsync($"{BaseUrl}/routes/create");

        // Wait for the create-route form to render. The address fields are the
        // AddressAutocomplete component, which renders a plain <input class="pt-ac-input">
        // rather than a RadzenTextBox — "input.rz-textbox" matches nothing on this page.
        var originInput = Page.Locator("input.pt-ac-input").First;
        await originInput.WaitForAsync(new() { Timeout = 20_000 });

        // Fill in origin and destination addresses
        // The form no longer has a standalone "Verify" button — address geocoding now
        // happens as part of "Save & Start Monitoring" submission.
        await originInput.ClickAsync();
        await Page.Keyboard.PressAsync("Control+A");
        await Page.Keyboard.PressAsync("Backspace");
        await Page.Keyboard.TypeAsync(OriginAddress);
        await Page.Keyboard.PressAsync("Tab");

        var destInput = Page.Locator("input.pt-ac-input").Nth(1);
        await destInput.ClickAsync();
        await Page.Keyboard.PressAsync("Control+A");
        await Page.Keyboard.PressAsync("Backspace");
        await Page.Keyboard.TypeAsync(DestinationAddress);
        await Page.Keyboard.PressAsync("Tab");

        // Click the Save & Start Monitoring submit button
        await Page.GetByRole(Microsoft.Playwright.AriaRole.Button, new() { Name = "Save & Start Monitoring" })
                  .ClickAsync();

        // ── Assert — either success (navigate to the new route) or inline error shown ──
        // With the stub geocoding provider in Testing env the creation may succeed or
        // fail. Either outcome is valid — what's NOT acceptable is a silent hang or Blazor crash.
        //
        // A successful save lands on /routes/{id}, not /dashboard: the page navigates to the
        // route it just created so the first probe is visible straight away.
        bool navigatedToRoute = false;
        try
        {
            await Page.WaitForURLAsync(new Regex(@"/routes/[0-9a-fA-F-]{36}"), new() { Timeout = 10_000 });
            navigatedToRoute = true;
        }
        catch (Exception)
        {
            // Didn't navigate — check for an inline error alert instead
        }

        if (!navigatedToRoute)
        {
            // An inline error from the API should be displayed in RadzenAlert
            ILocator errorAlert = Page.Locator(".rz-alert, [role='alert']").First;
            bool alertVisible = await errorAlert.IsVisibleAsync();
            string alertText = alertVisible ? await errorAlert.InnerTextAsync() : "(no alert)";
            Assert.True(alertVisible,
                $"Expected either navigation to the created route or an error alert after form submit, " +
                $"but neither occurred. URL: {Page.Url}. Alert text: '{alertText}'.");
        }

        // Guard: the "An unhandled error has occurred." Blazor error toast must NOT be visible
        bool blazorCrash = await Page.Locator("#blazor-error-ui").IsVisibleAsync();
        Assert.False(blazorCrash,
            "Blazor error toast appeared — the route creation endpoint returned an unexpected unhandled error.");
    }
}
