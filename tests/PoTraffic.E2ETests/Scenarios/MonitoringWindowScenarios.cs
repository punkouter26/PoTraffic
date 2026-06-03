using Microsoft.Playwright;
using PoTraffic.E2ETests.Helpers;

namespace PoTraffic.E2ETests.Scenarios;

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
///   dotnet test tests/PoTraffic.E2ETests --filter "FullyQualifiedName~MonitoringWindowScenarios"
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

        (string email, string password) = await api.SeedAdminAsync();
        (Guid routeId, _, _) = await api.SeedRouteAsync(email, OriginAddress, DestinationAddress);

        var consoleErrors = new List<string>();
        Page.Console += (_, msg) =>
        {
            if (msg.Type is "error" or "warning")
                consoleErrors.Add($"[{msg.Type.ToUpperInvariant()}] {msg.Text}");
        };
        Page.PageError += (_, err) => consoleErrors.Add($"[PAGE ERROR] {err}");

        // ── Act — log in via the UI ──────────────────────────────────────────────
        await Page.GotoAsync($"{BaseUrl}/login");

        ILocator emailInput = Page.Locator("input.rz-textbox").First;
        await emailInput.WaitForAsync(new() { Timeout = 90_000 });
        await emailInput.FillAsync(email);
        await Page.Locator("input[type='password']").FillAsync(password);
        await Page.GetByRole(AriaRole.Button, new() { Name = "Sign In" }).ClickAsync();
        await Page.WaitForURLAsync($"{BaseUrl}/dashboard", new() { Timeout = 30_000 });

        // ── Navigate to Route Detail page ────────────────────────────────────────
        await Page.GotoAsync($"{BaseUrl}/routes/{routeId}");

        // Wait for ROOT loading progress to disappear (splash screen)
        await Page.Locator(".loading-progress").WaitForAsync(new() { State = WaitForSelectorState.Detached, Timeout = 60_000 });

        // Wait for the WindowConfigPanel fieldset to appear — actual text is "Monitoring Schedule"
        ILocator fieldset = Page.Locator(".rz-fieldset", new() { HasText = "Monitoring Schedule" }).First;
        await fieldset.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 30_000 });

        // When no window exists, the form renders directly with "Save Schedule" button.
        // When editing an existing window, the button says "Save Changes".
        ILocator saveButton = fieldset.GetByRole(AriaRole.Button, new() { Name = "Save Schedule" });
        await saveButton.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 30_000 });

        // ── Set Start Time ────────────────────────────────────────────────────────
        // We find the input within the "Start Time" form field.
        ILocator startTimeInput = fieldset.Locator(".rz-form-field", new() { HasText = "Start Time" }).Locator("input").First;

        await startTimeInput.WaitForAsync(new() { Timeout = 15_000, State = WaitForSelectorState.Visible });
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

        // ── Verify days Mon–Fri are checked (default) ────────────────────────────
        // Radzen 10.x RadzenCheckBox renders a native <input type="checkbox"> that reflects
        // the bound value. Use Playwright's CSS :checked pseudo-class to count checked inputs.
        // The fieldset has 7 day checkboxes; Mon–Fri (5) should be checked by default.
        int checkedDays = await fieldset.Locator("input[type='checkbox']:checked").Count();
        Assert.True(checkedDays >= 5, $"Expected at least 5 days (Mon–Fri) checked by default, but found {checkedDays} checked.");

        // ── Click Save ────────────────────────────────────────────────────────────
        // Re-locate the save button (it may be "Save Schedule" or "Save Changes" depending on state)
        ILocator actualSave = fieldset.GetByRole(AriaRole.Button, new() { Name = "Save" });
        await actualSave.ClickAsync();

        // ── Assert — no error alert rendered ─────────────────────────────────────
        // Give the API call time to complete (201 Created or 409 Conflict if window already exists)
        await Page.WaitForTimeoutAsync(2_000);

        ILocator errorAlert = Page.Locator(".rz-alert-danger");
        bool errorVisible = await errorAlert.IsVisibleAsync();
        string alertText = errorVisible ? await errorAlert.InnerTextAsync() : string.Empty;
        Assert.False(errorVisible,
            $"Expected no error alert after saving, but got: {alertText}");

        // ── Assert — no JavaScript errors during the flow ─────────────────────────
        Assert.DoesNotContain(consoleErrors,
            m => m.StartsWith("[ERROR]", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Validation path: submitting an end time that is before the start time shows an
    /// inline validation error and does NOT navigate away from the route detail page.
    /// </summary>
    [SkipUnlessE2EReady]
    public async Task SetMonitoringWindow_EndBeforeStart_ShowsValidationError()
    {
        // ── Arrange ─────────────────────────────────────────────────────────────
        using HttpClient apiHttp = new() { BaseAddress = new Uri(BaseUrl) };
        TestingApiClient api = new(apiHttp);

        (string email, string password) = await api.SeedAdminAsync();
        (Guid routeId, _, _) = await api.SeedRouteAsync(email, OriginAddress, DestinationAddress);

        var consoleErrors = new List<string>();
        Page.Console += (_, msg) =>
        {
            if (msg.Type is "error")
                consoleErrors.Add($"[ERROR] {msg.Text}");
        };
        Page.PageError += (_, err) => consoleErrors.Add($"[PAGE ERROR] {err}");

        // ── Act — log in ─────────────────────────────────────────────────────────
        await Page.GotoAsync($"{BaseUrl}/login");
        ILocator emailInput = Page.Locator("input.rz-textbox").First;
        await emailInput.WaitForAsync(new() { Timeout = 90_000 });
        await emailInput.FillAsync(email);
        await Page.Locator("input[type='password']").FillAsync(password);
        await Page.GetByRole(AriaRole.Button, new() { Name = "Sign In" }).ClickAsync();
        await Page.WaitForURLAsync($"{BaseUrl}/dashboard", new() { Timeout = 30_000 });

        // Navigate to route detail
        await Page.GotoAsync($"{BaseUrl}/routes/{routeId}");

        // Wait for ROOT loading progress to disappear (splash screen)
        await Page.Locator(".loading-progress").WaitForAsync(new() { State = WaitForSelectorState.Detached, Timeout = 60_000 });

        // Wait for the WindowConfigPanel fieldset to appear — actual text is "Monitoring Schedule"
        ILocator fieldset = Page.Locator(".rz-fieldset", new() { HasText = "Monitoring Schedule" }).First;
        await fieldset.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 30_000 });

        // Wait for the form to be ready — "Save Schedule" when no window exists
        await fieldset.GetByRole(AriaRole.Button, new() { Name = "Save Schedule" }).WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 30_000 });

        // Set end time BEFORE start time
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
        await Page.Keyboard.TypeAsync("07:00"); // end before start
        await Page.Keyboard.PressAsync("Enter");
        await Page.Keyboard.PressAsync("Tab");

        // ── Click Save ────────────────────────────────────────────────────────────
        // Button says "Save Schedule" (new) or "Save Changes" (editing existing)
        await fieldset.GetByRole(AriaRole.Button, new() { Name = "Save" }).ClickAsync();
        await Page.WaitForTimeoutAsync(3_000);

        // ── Assert — inline error message is shown ────────────────────────────────
        // RadzenAlert with AlertStyle.Danger renders with BOTH class "rz-alert-danger" 
        // OR role="alert" depending on configuration/version.
        ILocator errorAlert = Page.Locator(".rz-alert, .rz-alert-danger, [role='alert']").First;
        await errorAlert.WaitForAsync(new() { Timeout = 10_000 });
        string errorText = await errorAlert.InnerTextAsync();

        Assert.Contains("end time", errorText, StringComparison.OrdinalIgnoreCase);

        // Still on the same page
        Assert.Contains($"/routes/{routeId}", Page.Url, StringComparison.OrdinalIgnoreCase);

        // No JS errors
        Assert.DoesNotContain(consoleErrors,
            m => m.StartsWith("[ERROR]", StringComparison.OrdinalIgnoreCase));
    }
}
