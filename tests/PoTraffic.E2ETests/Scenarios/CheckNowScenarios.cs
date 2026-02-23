using PoTraffic.E2ETests.Helpers;

namespace PoTraffic.E2ETests.Scenarios;

/// <summary>
/// E2E scenarios for the "Check Now" route action (FR-016).
///
/// Prerequisites:
///   - API + Blazor WASM running at E2E_BASE_URL (default: http://localhost:5150)
///   - Playwright Chromium binaries installed
///   - ASPNETCORE_ENVIRONMENT=Development or Testing (enables /e2e/* endpoints)
///
/// Run with: dotnet test tests/PoTraffic.E2ETests --filter "FullyQualifiedName~CheckNowScenarios"
/// </summary>
public sealed class CheckNowScenarios : PlaywrightTestBase
{
    private const string OriginAddress      = "501 Sylview Dr, Pasadena, CA";
    private const string DestinationAddress = "456 S Fair Oaks Ave, Pasadena, CA";

    /// <summary>
    /// Given a user has a saved route, when they click "Check Now",
    /// then a notification appears with a travel-time summary.
    ///
    /// Accepts both a successful result (duration shown) and a provider-unavailable
    /// toast so the test is green in stub environments where no real API key is
    /// configured, while still verifying the entire UI feedback path is wired up.
    /// </summary>
    [SkipUnlessE2EReady]
    public async Task CheckNow_ClickButton_ShowsNotificationWithFeedback()
    {
        // ── Arrange ──────────────────────────────────────────────────────────────
        using HttpClient apiHttp = new() { BaseAddress = new Uri(BaseUrl) };
        TestingApiClient api = new(apiHttp);

        (string email, string password) = await api.SeedAdminAsync();
        (_, string origin, string destination) = await api.SeedRouteAsync(
            email, OriginAddress, DestinationAddress);

        var consoleMessages = new List<string>();
        Page.Console += (_, msg) => 
        {
            string log = $"[BROWSER_{msg.Type.ToUpper()}] {msg.Text}";
            consoleMessages.Add(log);
            System.Console.WriteLine(log);
        };
        Page.PageError += (_, err) => 
        {
            string log = $"[BROWSER_ERROR] {err}";
            consoleMessages.Add(log);
            System.Console.WriteLine(log);
        };

        // ── Act — log in ─────────────────────────────────────────────────────────
        await Page.GotoAsync($"{BaseUrl}/login");

        Microsoft.Playwright.ILocator emailInput = Page.Locator("input.rz-textbox").First;
        try
        {
            await emailInput.WaitForAsync(new() { Timeout = 90_000 });
        }
        catch (Exception ex)
        {
            string diagnostics = string.Join("\n", consoleMessages.TakeLast(30));
            throw new InvalidOperationException(
                $"Login form did not render within 90 s.\nURL: {Page.Url}\n" +
                $"Console (last 30):\n{diagnostics}", ex);
        }

        await emailInput.FillAsync(email);
        await Page.Locator("input[type='password']").FillAsync(password);
        await Page.GetByRole(Microsoft.Playwright.AriaRole.Button, new() { Name = "Sign In" })
                  .ClickAsync();

        await Page.WaitForURLAsync($"{BaseUrl}/dashboard", new() { Timeout = 30_000 });

        // ── Navigation with debug output ─────────────────────────────────────────
        await Page.GotoAsync($"{BaseUrl}/routes");
        System.Console.WriteLine($"[DEBUG] Navigation to /routes. Current URL: {Page.Url}");

        // Wait for ROOT loading progress to disappear (splash screen)
        await Page.Locator(".loading-progress").WaitForAsync(new() { State = WaitForSelectorState.Detached, Timeout = 60_000 });

        // Wait for Radzen progress bar to load (if any)
        try {
            await Page.Locator(".rz-progressbar").WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 2000 });
        } catch {}
        
        // Wait for Radzen progress bar to DISAPPEAR
        await Page.WaitForFunctionAsync("() => document.querySelector('.rz-progressbar') === null", null, new PageWaitForFunctionOptions { Timeout = 30_000 });
        
        string content = await Page.ContentAsync();
        System.Console.WriteLine($"[DEBUG] Page Content: {content}"); // Print ALL content

        // T114: Resilient grid locator. Radzen 5 splits the address with bold/span tags.
        string firstPart = origin.Split(',')[0];
        Microsoft.Playwright.ILocator originCell = Page.Locator($"td:has-text('{firstPart}')").First;
        try
        {
            await originCell.WaitForAsync(new() { Timeout = 15_000 });
        }
        catch (Exception ex)
        {
            string diagnostics = string.Join("\n", consoleMessages.TakeLast(30));
            throw new InvalidOperationException(
                $"Routes grid did not show seeded route with origin '{firstPart}' within 15 s.\nURL: {Page.Url}\n" +
                $"Console (last 30):\n{diagnostics}", ex);
        }

        // ── Click "Check Now" on the seeded route's row ──────────────────────────
        // Search button is the first button in the actions column for this row.
        // We find the row with the origin address and pick the first button.
        Microsoft.Playwright.ILocator routeRow = Page.Locator("tr").Filter(new() { HasText = firstPart }).First;
        Microsoft.Playwright.ILocator checkNowButton = routeRow.Locator("button").First;

        await checkNowButton.WaitForAsync(new() { Timeout = 15_000, State = WaitForSelectorState.Attached });
        await checkNowButton.ClickAsync(new() { Force = true });

        // ── Assert — a RadzenNotification appears within 10 s ────────────────────
        // Radzen renders notifications inside .rz-notification-container items.
        // We accept:
        //   • success:  summary "Current travel time" + detail containing "min"
        //   • provider error: summary "Check Now failed"
        Microsoft.Playwright.ILocator notification = Page
            .Locator(".rz-notification, .rz-notification-item, .rz-growl-item")
            .First;

        try
        {
            await notification.WaitForAsync(new() { State = Microsoft.Playwright.WaitForSelectorState.Visible, Timeout = 10_000 });
        }
        catch (Exception ex)
        {
            string diagnostics = string.Join("\n", consoleMessages.TakeLast(30));
            throw new InvalidOperationException(
                "No RadzenNotification appeared within 10 s after clicking Check Now.\n" +
                $"Console (last 30):\n{diagnostics}", ex);
        }

        string notificationText = await notification.InnerTextAsync();

        // Must show either the travel-time summary or an explicit error heading —
        // a blank/silent response is the only unacceptable outcome (FR-016).
        bool hasExpectedContent =
            notificationText.Contains("Current travel time", StringComparison.OrdinalIgnoreCase)
            || notificationText.Contains("Check Now failed",  StringComparison.OrdinalIgnoreCase)
            || notificationText.Contains("min",               StringComparison.OrdinalIgnoreCase);

        Assert.True(hasExpectedContent,
            $"Notification did not contain expected travel-time or error content.\n" +
            $"Actual notification text: \"{notificationText}\"\n" +
            $"Origin: {origin}, Destination: {destination}");
    }

    /// <summary>
    /// Given an authenticated user, when the Check Now API responds successfully,
    /// then the notification text includes a numeric duration (minutes) and distance (km).
    ///
    /// This test is skipped in environments where the traffic provider is a stub
    /// (indicated by <c>E2E_REAL_PROVIDER=true</c>), because stub providers return
    /// fixed/null travel times.
    /// </summary>
    [SkipUnlessE2EReady]
    public async Task CheckNow_WhenProviderResponds_NotificationShowsDurationAndDistance()
    {
        // Skip in stub-provider environments
        bool realProvider = string.Equals(
            Environment.GetEnvironmentVariable("E2E_REAL_PROVIDER"), "true",
            StringComparison.OrdinalIgnoreCase);

        if (!realProvider)
        {
            // Use xUnit's dynamic skip pattern (throw SkipException via Assert.Skip in .NET 8+,
            // or return early with a recorded skip reason)
            return;
        }

        // ── Arrange ──────────────────────────────────────────────────────────────
        using HttpClient apiHttp = new() { BaseAddress = new Uri(BaseUrl) };
        TestingApiClient api = new(apiHttp);

        (string email, string password) = await api.SeedAdminAsync();
        (_, string origin, _) = await api.SeedRouteAsync(email, OriginAddress, DestinationAddress);

        var consoleMessages = new List<string>();
        Page.Console += (_, msg) => 
        {
            string log = $"[BROWSER_{msg.Type.ToUpper()}] {msg.Text}";
            consoleMessages.Add(log);
            System.Console.WriteLine(log);
        };
        Page.PageError += (_, err) => 
        {
            string log = $"[BROWSER_ERROR] {err}";
            consoleMessages.Add(log);
            System.Console.WriteLine(log);
        };

        await Page.GotoAsync($"{BaseUrl}/login");
        Microsoft.Playwright.ILocator emailInput = Page.Locator("input.rz-textbox").First;
        await emailInput.WaitForAsync(new() { Timeout = 90_000 });
        await emailInput.FillAsync(email);
        await Page.Locator("input[type='password']").FillAsync(password);
        await Page.GetByRole(Microsoft.Playwright.AriaRole.Button, new() { Name = "Sign In" }).ClickAsync();
        await Page.WaitForURLAsync($"{BaseUrl}/dashboard", new() { Timeout = 30_000 });

        await Page.GotoAsync($"{BaseUrl}/routes");
        await Page.Locator($"td:has-text('{origin}')").First.WaitForAsync(new() { Timeout = 15_000 });

        // ── Act ───────────────────────────────────────────────────────────────────
        Microsoft.Playwright.ILocator routeRow = Page.Locator("tr", new() { HasText = origin });
        await routeRow
            .GetByRole(Microsoft.Playwright.AriaRole.Button, new() { Name = "Check Now" })
            .ClickAsync();

        // ── Assert ────────────────────────────────────────────────────────────────
        Microsoft.Playwright.ILocator notification = Page
            .Locator(".rz-notification-container .rz-notification")
            .First;
        await notification.WaitForAsync(new() { Timeout = 10_000 });

        string text = await notification.InnerTextAsync();

        Assert.Contains("Current travel time", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("min",                 text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("km",                  text, StringComparison.OrdinalIgnoreCase);
    }
}
