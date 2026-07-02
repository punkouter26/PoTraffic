using PoTraffic.Tests.E2E.Helpers;

namespace PoTraffic.Tests.E2E.Scenarios;

/// <summary>
/// E2E scenarios for the "Check Now" route action (FR-016).
///
/// Prerequisites:
///   - API + Blazor WASM running at E2E_BASE_URL (default: http://localhost:5150)
///   - Playwright Chromium binaries installed
///   - ASPNETCORE_ENVIRONMENT=Development or Testing (enables /e2e/* endpoints)
///
/// Run with: dotnet test tests/PoTraffic.E2EUI --filter "FullyQualifiedName~CheckNowScenarios"
/// </summary>
public sealed class CheckNowScenarios : PlaywrightTestBase
{
    private const string OriginAddress = "501 Sylview Dr, Pasadena, CA";
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

        string email = await api.SeedAdminAsync();
        Assert.NotNull(await api.DevLoginAsync(email, role: "Administrator"));

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

        // ── Act — authenticate ──────────────────────────────────────────────────
        await AuthenticateViaDevLoginAsync(email);
        await Page.WaitForURLAsync($"{BaseUrl}/dashboard", new() { Timeout = 30_000 });

        // ── Navigation with debug output ─────────────────────────────────────────
        // /routes redirects to /dashboard (C1/UX-6 merge). Routes render as .pt-route-card.
        await Page.GotoAsync($"{BaseUrl}/routes");
        await Page.WaitForURLAsync($"{BaseUrl}/dashboard", new() { Timeout = 15_000 });
        System.Console.WriteLine($"[DEBUG] Navigation to /routes → /dashboard. Current URL: {Page.Url}");

        // Wait for the status bar to appear (dashboard finished loading)
        await Page.Locator(".pt-status-bar").WaitForAsync(new() { Timeout = 30_000 });

        // T114: Find the route card by origin address (pt-route-addr-from span)
        string firstPart = origin.Split(',')[0];
        Microsoft.Playwright.ILocator routeCard =
            Page.Locator(".pt-route-card").Filter(new() { HasText = firstPart }).First;
        try
        {
            await routeCard.WaitForAsync(new() { Timeout = 15_000 });
        }
        catch (Exception ex)
        {
            string diagnostics = string.Join("\n", consoleMessages.TakeLast(30));
            throw new InvalidOperationException(
                $"Route card with origin '{firstPart}' did not appear on dashboard within 15 s.\nURL: {Page.Url}\n" +
                $"Console (last 30):\n{diagnostics}", ex);
        }

        // ── Click "Check Now" on the seeded route's card ─────────────────────────
        // Route cards render a RadzenButton with Text="Check Now" in .pt-route-card-actions.
        // We scope the lookup to the specific card that contains the origin address.
        Microsoft.Playwright.ILocator checkNowButton =
            routeCard.GetByRole(Microsoft.Playwright.AriaRole.Button, new() { Name = "Check Now" });

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
            || notificationText.Contains("Check Now failed", StringComparison.OrdinalIgnoreCase)
            || notificationText.Contains("min", StringComparison.OrdinalIgnoreCase);

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
        // Skip in stub-provider environments — the test asserts a *numeric* duration/distance
        // which a stub provider cannot produce.
        bool realProvider = string.Equals(
            Environment.GetEnvironmentVariable("E2E_REAL_PROVIDER"), "true",
            StringComparison.OrdinalIgnoreCase);

        if (!realProvider)
        {
            // Early return — no real provider available, nothing to assert.
            // Console output documents the skip reason for CI logs.
            Console.WriteLine("SKIP: E2E_REAL_PROVIDER is not set — stub providers cannot produce numeric duration/distance.");
            return;
        }

        // ── Arrange ──────────────────────────────────────────────────────────────
        using HttpClient apiHttp = new() { BaseAddress = new Uri(BaseUrl) };
        TestingApiClient api = new(apiHttp);

        string email = await api.SeedAdminAsync();
        Assert.NotNull(await api.DevLoginAsync(email, role: "Administrator"));

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

        await AuthenticateViaDevLoginAsync(email);
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
        Assert.Contains("min", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("km", text, StringComparison.OrdinalIgnoreCase);
    }
}
