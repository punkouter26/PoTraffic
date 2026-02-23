using PoTraffic.E2ETests.Helpers;

namespace PoTraffic.E2ETests.Scenarios;

/// <summary>
/// E2E scenarios for the Add Route user story.
///
/// Prerequisites:
///   - API + Blazor WASM running at E2E_BASE_URL (default: http://localhost:5150)
///   - Playwright Chromium binaries installed
///   - ASPNETCORE_ENVIRONMENT=Development or Testing (enables /e2e/* endpoints)
///
/// Run with: dotnet test tests/PoTraffic.E2ETests --filter "FullyQualifiedName~CreateRouteScenarios"
/// </summary>
public sealed class CreateRouteScenarios : PlaywrightTestBase
{
    private const string OriginAddress      = "501 Sylview Dr, Pasadena, CA";
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
        (string email, string password) = await api.SeedAdminAsync();
        (_, string origin, string destination) = await api.SeedRouteAsync(
            email, OriginAddress, DestinationAddress);

        var consoleMessages = new List<string>();
        Page.Console += (_, msg) => consoleMessages.Add($"[{msg.Type}] {msg.Text}");
        Page.PageError += (_, err) => consoleMessages.Add($"[PAGE ERROR] {err}");

        // ── Act — log in via the UI ──────────────────────────────────────────────
        await Page.GotoAsync($"{BaseUrl}/login");

        var emailInput = Page.Locator("input.rz-textbox").First;
        try
        {
            await emailInput.WaitForAsync(new() { Timeout = 90_000 });
        }
        catch (Exception ex)
        {
            string diagnostics = string.Join("\n", consoleMessages.TakeLast(30));
            throw new InvalidOperationException(
                $"Login form did not render within 90s.\nURL: {Page.Url}\n" +
                $"Console (last 30):\n{diagnostics}", ex);
        }

        await emailInput.FillAsync(email);
        await Page.Locator("input[type='password']").FillAsync(password);
        await Page.GetByRole(Microsoft.Playwright.AriaRole.Button, new() { Name = "Sign In" })
                  .ClickAsync();

        await Page.WaitForURLAsync($"{BaseUrl}/dashboard", new() { Timeout = 30_000 });

        // Navigate to /routes
        await Page.GotoAsync($"{BaseUrl}/routes");

        // ── Assert — both addresses visible in the RadzenDataGrid ────────────────
        // We look for cells containing the street name (ignoring commas/spacing for now)
        string originStreet = origin.Split(',').First();
        Microsoft.Playwright.ILocator originCell =
            Page.Locator("td").Filter(new() { HasText = originStreet }).First;
        await originCell.WaitForAsync(new() { Timeout = 15_000, State = Microsoft.Playwright.WaitForSelectorState.Visible });

        bool originVisible = await originCell.IsVisibleAsync();
        Assert.True(originVisible,
            $"Expected origin street '{originStreet}' to be visible in the routes grid.");

        string destStreet = destination.Split(',').First();
        Microsoft.Playwright.ILocator destCell =
            Page.Locator("td").Filter(new() { HasText = destStreet }).First;
        await destCell.WaitForAsync(new() { Timeout = 15_000, State = Microsoft.Playwright.WaitForSelectorState.Visible });

        bool destVisible = await destCell.IsVisibleAsync();
        Assert.True(destVisible,
            $"Expected destination street '{destStreet}' to be visible in the routes grid.");
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
        (string email, string password) = await api.SeedAdminAsync();

        var consoleMessages = new List<string>();
        Page.Console += (_, msg) => consoleMessages.Add($"[{msg.Type}] {msg.Text}");
        Page.PageError += (_, err) => consoleMessages.Add($"[PAGE ERROR] {err}");

        // ── Act — log in then navigate to create-route form ─────────────────────
        await Page.GotoAsync($"{BaseUrl}/login");

        var emailInput = Page.Locator("input.rz-textbox").First;
        try
        {
            await emailInput.WaitForAsync(new() { Timeout = 90_000 });
        }
        catch (Exception ex)
        {
            string diagnostics = string.Join("\n", consoleMessages.TakeLast(30));
            throw new InvalidOperationException(
                $"Login form did not render.\nURL: {Page.Url}\n" +
                $"Console (last 30):\n{diagnostics}", ex);
        }

        await emailInput.FillAsync(email);
        await Page.Locator("input[type='password']").FillAsync(password);
        await Page.GetByRole(Microsoft.Playwright.AriaRole.Button, new() { Name = "Sign In" })
                  .ClickAsync();

        await Page.WaitForURLAsync($"{BaseUrl}/dashboard", new() { Timeout = 30_000 });

        await Page.GotoAsync($"{BaseUrl}/routes/create");

        // Wait for the create-route form to render (first RadzenTextBox = Origin input)
        var originInput = Page.Locator("input.rz-textbox").First;
        await originInput.WaitForAsync(new() { Timeout = 20_000 });

        // Fill in origin address and click the first Verify button
        await originInput.ClickAsync();
        await Page.Keyboard.PressAsync("Control+A");
        await Page.Keyboard.PressAsync("Backspace");
        await Page.Keyboard.TypeAsync(OriginAddress);
        await Page.Keyboard.PressAsync("Tab");

        await Page.GetByRole(Microsoft.Playwright.AriaRole.Button, new() { Name = "Verify" })
                  .First
                  .ClickAsync();

        // ── Assert — a response message is visible (not a crash or silent 404) ───
        // RadzenAlert renders as <div role="alert"> with AlertStyle.Danger for both
        // success ("verified ✓") and failure ("could not be verified") outcomes.
        // We wait for the alert text to contain either expected phrase; this avoids false
        // matches on unrelated alerts (nav banners, previous state, etc.).
        await Page.WaitForFunctionAsync(
            @"() => {
                const alerts = [...document.querySelectorAll('.rz-alert, [role=""alert""]')];
                return alerts.some(el => {
                    const t = el.innerText.toLowerCase();
                    return t.includes('verified') || t.includes('could not');
                });
            }",
            null,
            new() { Timeout = 20_000 });

        var alertDiv = Page.Locator(".rz-alert, [role='alert']")
            .Filter(new() { HasTextRegex = new System.Text.RegularExpressions.Regex("verified|could not", System.Text.RegularExpressions.RegexOptions.IgnoreCase) })
            .First;
        string alertText = await alertDiv.InnerTextAsync();

        bool hasResponse = alertText.Contains("verified", StringComparison.OrdinalIgnoreCase) ||
                           alertText.Contains("could not be verified", StringComparison.OrdinalIgnoreCase);

        Assert.True(hasResponse,
            $"Expected a verify response message but got: '{alertText}'\n" +
            $"Console: {string.Join("; ", consoleMessages.TakeLast(10))}");

        // Guard: the "An unhandled error has occurred." Blazor error toast must NOT be visible
        bool blazorCrash = await Page.Locator("#blazor-error-ui").IsVisibleAsync();
        Assert.False(blazorCrash,
            "Blazor error toast appeared — the verify endpoint likely returned an unexpected error.");
    }
}
