using Microsoft.Playwright;
using PoTraffic.E2ETests.Ui.Helpers;

namespace PoTraffic.E2ETests.Ui.Scenarios;

/// <summary>
/// E2E scenarios for the Monitoring Window configuration flow:
/// setting start time, end time, and days-of-week on the Route Detail page.
///
/// Prerequisites:
///   - API + Blazor WASM running at E2E_BASE_URL (default: http://localhost:5150)
///   - Playwright Chromium binaries installed
///   - ASPNETCORE_ENVIRONMENT=Development or Testing (enables /e2e/* seeding endpoints)
///
/// Run with:
///   dotnet test tests/PoTraffic.E2EUI --filter "FullyQualifiedName~MonitoringWindowScenarios"
/// </summary>
public sealed class MonitoringWindowScenarios : PlaywrightTestBase
{
    private const string OriginAddress = "501 Sylview Dr, Pasadena, CA";
    private const string DestinationAddress = "456 S Fair Oaks Ave, Pasadena, CA";

    /// <summary>
    /// Happy path: navigates to the route detail page, sets a start time of 08:00 and
    /// end time of 10:00 for Mon–Fri, saves the window, and verifies no error is shown.
    /// Also asserts no JavaScript errors were emitted during the interaction.
    /// </summary>
    [SkipUnlessE2EReady]
    public async Task SetMonitoringWindow_ValidTimes_SavesSuccessfully()
    {
        // ── Arrange ─────────────────────────────────────────────────────────────
        using HttpClient apiHttp = new() { BaseAddress = new Uri(BaseUrl) };
        TestingApiClient api = new(apiHttp);

        string email = await api.SeedAdminAsync();
        Assert.NotNull(await api.DevLoginAsync(email, role: "Administrator"));

        (RouteId routeId, _, _) = await api.SeedRouteAsync(email, OriginAddress, DestinationAddress);

        var consoleErrors = new List<string>();
        Page.Console += (_, msg) =>
        {
            if (msg.Type is "error" or "warning")
                consoleErrors.Add($"[{msg.Type.ToUpperInvariant()}] {msg.Text}");
        };
        Page.PageError += (_, err) => consoleErrors.Add($"[PAGE ERROR] {err}");

        // ── Act — authenticate through the Testing-only token path ──────────────
        await AuthenticateViaDevLoginAsync(email);
        await Page.WaitForURLAsync($"{BaseUrl}/dashboard", new() { Timeout = 30_000 });

        // ── Navigate to Route Detail page ────────────────────────────────────────
        await Page.GotoAsync($"{BaseUrl}/routes/{routeId}");

        // Wait for ROOT loading progress to disappear (splash screen) — only if present
        ILocator loadingProgress = Page.Locator(".loading-progress");
        if (await loadingProgress.CountAsync() > 0)
        {
            await loadingProgress.WaitForAsync(new() { State = WaitForSelectorState.Detached, Timeout = 60_000 });
        }

        // Wait for the WindowConfigPanel — the schedule row of the route page's settings card.
        ILocator fieldset = Page.Locator(".pt-schedule").First;
        await fieldset.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 45_000 });

        // With no window the form renders directly; with one, "Edit schedule" opens it.
        await OpenScheduleFormAsync(fieldset);

        // ── Set Start Time ────────────────────────────────────────────────────────
        // We find the input within the "Start Time" form field.
        ILocator startTimeInput = fieldset.Locator(".rz-form-field", new() { HasText = "Start Time" }).Locator("input").First;

        await startTimeInput.WaitForAsync(new() { Timeout = 20_000, State = WaitForSelectorState.Visible });
        await startTimeInput.ClickAsync();
        await Page.Keyboard.PressAsync("Control+A");
        await Page.Keyboard.PressAsync("Backspace");
        await Page.Keyboard.TypeAsync("08:00");
        await Page.Keyboard.PressAsync("Enter");
        await Page.Keyboard.PressAsync("Tab");

        // ── Set End Time ──────────────────────────────────────────────────────────
        // We find the input within the "End Time" form field.
        ILocator endTimeInput = fieldset.Locator(".rz-form-field", new() { HasText = "End Time" }).Locator("input").First;

        await endTimeInput.WaitForAsync(new() { Timeout = 15_000, State = WaitForSelectorState.Visible });
        await endTimeInput.ClickAsync();
        await Page.Keyboard.PressAsync("Control+A");
        await Page.Keyboard.PressAsync("Backspace");
        await Page.Keyboard.TypeAsync("10:00");
        await Page.Keyboard.PressAsync("Enter");
        await Page.Keyboard.PressAsync("Tab");

        // ── Verify days Mon–Fri are selected (default) ───────────────────────────
        // The days are a multi-select RadzenSelectBar (shared with New Route); a selected
        // item is a button carrying rz-state-active. Mon–Fri (5) are on by default.
        int checkedDays = await fieldset.Locator(".pt-day-toggles .rz-state-active").CountAsync();
        Assert.True(checkedDays >= 5, $"Expected at least 5 days (Mon–Fri) selected by default, but found {checkedDays}.");

        // ── Click Save ────────────────────────────────────────────────────────────
        ILocator actualSave = fieldset.GetByRole(AriaRole.Button, new() { Name = "Save" });
        await actualSave.ClickAsync();

        // ── Assert — no error alert rendered ─────────────────────────────────────
        // Give the API call time to complete (201 Created or 409 Conflict if window already exists)
        await Page.WaitForTimeoutAsync(2_000);

        ILocator errorAlert = fieldset.Locator("[role='alert'], .rz-messages-error").First;
        bool errorVisible = await errorAlert.IsVisibleAsync();
        string alertText = errorVisible ? await errorAlert.InnerTextAsync() : string.Empty;
        Assert.False(errorVisible,
            $"Expected no error alert after saving, but got: {alertText}");

        // ── Assert — no JavaScript errors during the flow ─────────────────────────
        Assert.DoesNotContain(consoleErrors,
            m => m.StartsWith("[ERROR]", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Validation path: submitting an end time equal to the start time shows an inline
    /// validation error and does NOT navigate away from the route detail page.
    ///
    /// <para>End-before-start is not an error: a window may wrap midnight (21:00 → 03:00),
    /// and the server accepts it. Only a zero-length window is unusable.</para>
    /// </summary>
    [SkipUnlessE2EReady]
    public async Task SetMonitoringWindow_EndEqualsStart_ShowsValidationError()
    {
        // ── Arrange ─────────────────────────────────────────────────────────────
        using HttpClient apiHttp = new() { BaseAddress = new Uri(BaseUrl) };
        TestingApiClient api = new(apiHttp);

        string email = await api.SeedAdminAsync();
        Assert.NotNull(await api.DevLoginAsync(email, role: "Administrator"));

        (RouteId routeId, _, _) = await api.SeedRouteAsync(email, OriginAddress, DestinationAddress);

        var consoleErrors = new List<string>();
        Page.Console += (_, msg) =>
        {
            if (msg.Type is "error")
                consoleErrors.Add($"[ERROR] {msg.Text}");
        };
        Page.PageError += (_, err) => consoleErrors.Add($"[PAGE ERROR] {err}");

        // ── Act — authenticate ──────────────────────────────────────────────────
        await AuthenticateViaDevLoginAsync(email);
        await Page.WaitForURLAsync($"{BaseUrl}/dashboard", new() { Timeout = 30_000 });

        // Navigate to route detail
        await Page.GotoAsync($"{BaseUrl}/routes/{routeId}");

        // Wait for ROOT loading progress to disappear (splash screen) — only if present
        ILocator loadingProgress = Page.Locator(".loading-progress");
        if (await loadingProgress.CountAsync() > 0)
        {
            await loadingProgress.WaitForAsync(new() { State = WaitForSelectorState.Detached, Timeout = 60_000 });
        }

        // Wait for the WindowConfigPanel — the schedule row of the route page's settings card.
        ILocator fieldset = Page.Locator(".pt-schedule").First;
        await fieldset.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 45_000 });

        await OpenScheduleFormAsync(fieldset);

        // Set end time EQUAL to start time
        ILocator startTimeInput = fieldset.Locator(".rz-form-field", new() { HasText = "Start Time" }).Locator("input").First;

        await startTimeInput.ClickAsync();
        await Page.Keyboard.PressAsync("Control+A");
        await Page.Keyboard.PressAsync("Backspace");
        await Page.Keyboard.TypeAsync("09:00");
        await Page.Keyboard.PressAsync("Enter");
        await Page.Keyboard.PressAsync("Tab");

        ILocator endTimeInput = fieldset.Locator(".rz-form-field", new() { HasText = "End Time" }).Locator("input").First;

        await endTimeInput.ClickAsync();
        await Page.Keyboard.PressAsync("Control+A");
        await Page.Keyboard.PressAsync("Backspace");
        await Page.Keyboard.TypeAsync("09:00"); // zero-length window
        await Page.Keyboard.PressAsync("Enter");
        await Page.Keyboard.PressAsync("Tab");

        // ── Click Save ────────────────────────────────────────────────────────────
        await fieldset.GetByRole(AriaRole.Button, new() { Name = "Save" }).ClickAsync();
        await Page.WaitForTimeoutAsync(3_000);

        // ── Assert — inline error message is shown ────────────────────────────────
        // The message comes from the end-time field's RadzenCustomValidator, which renders
        // as .rz-messages-error. Filtered by text so nothing else in the panel can match.
        ILocator errorAlert = fieldset
            .Locator(".rz-messages-error, [role='alert']")
            .Filter(new() { HasTextString = "end time" })
            .First;

        await errorAlert.WaitForAsync(new() { Timeout = 10_000 });
        string errorText = await errorAlert.InnerTextAsync();

        Assert.Contains("end time", errorText, StringComparison.OrdinalIgnoreCase);

        // Still on the same page
        Assert.Contains($"/routes/{routeId}", Page.Url, StringComparison.OrdinalIgnoreCase);

        // No JS errors
        Assert.DoesNotContain(consoleErrors,
            m => m.StartsWith("[ERROR]", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Leaves WindowConfigPanel showing its schedule form, whichever state it started in.
    ///
    /// <para>
    /// The panel renders a one-line summary behind an "Edit schedule" button when the route
    /// already has a window, and the form directly when it does not. Route seeding is
    /// idempotent and Azurite persists between runs, so which of the two a test meets depends
    /// on whether an earlier run saved a schedule — waiting for Save without opening the form
    /// first passes on a clean volume and fails on every run after it.
    /// </para>
    /// </summary>
    private static async Task OpenScheduleFormAsync(ILocator fieldset)
    {
        // Wait for the panel to settle first: any button inside it means one of the two
        // states has rendered. Checking for Edit before that would skip the click and then
        // wait out the clock on a Save button the summary view was never going to show.
        await fieldset.Locator("button").First
            .WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 30_000 });

        ILocator editButton = fieldset.GetByRole(AriaRole.Button, new() { Name = "Edit schedule" });
        if (await editButton.CountAsync() > 0 && await editButton.First.IsVisibleAsync())
        {
            await editButton.First.ClickAsync();
        }

        await fieldset.GetByRole(AriaRole.Button, new() { Name = "Save" }).First
            .WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 20_000 });
    }
}
