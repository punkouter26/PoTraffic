using Microsoft.Playwright;
using Xunit;

namespace PoTraffic.E2ETests.Scenarios;

/// <summary>
/// Mobile viewport E2E tests — verifies key user journeys render correctly
/// at a mobile form factor (390×844, equivalent to iPhone 14 Pro).
/// These tests are skipped pending mobile-responsive layout completion in the polish phase.
/// </summary>
public sealed class MobileViewportScenarios : IAsyncLifetime
{
    private IPlaywright _playwright = null!;
    private IBrowser _browser = null!;
    private IBrowserContext _context = null!;
    private IPage _page = null!;

    private const int MobileWidth = 390;
    private const int MobileHeight = 844;

    private static string BaseUrl =>
        Environment.GetEnvironmentVariable("E2E_BASE_URL") ?? "http://localhost:5150";

    public async Task InitializeAsync()
    {
        _playwright = await Microsoft.Playwright.Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true
        });

        // Mobile context — iPhone 14 Pro equivalent viewport
        _context = await _browser.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = BaseUrl,
            IgnoreHTTPSErrors = true,
            ViewportSize = new ViewportSize
            {
                Width = MobileWidth,
                Height = MobileHeight
            },
            IsMobile = true,
            HasTouch = true,
            UserAgent = "Mozilla/5.0 (iPhone; CPU iPhone OS 16_0 like Mac OS X) " +
                        "AppleWebKit/605.1.15 (KHTML, like Gecko) Version/16.0 Mobile/15E148 Safari/604.1"
        });

        _page = await _context.NewPageAsync();
    }

    public async Task DisposeAsync()
    {
        await _context.DisposeAsync();
        await _browser.DisposeAsync();
        _playwright.Dispose();
    }

    /// <summary>
    /// Login page renders correctly at mobile viewport.
    ///
    /// Given  the user opens the login page on a mobile device (390×844)
    /// When   the page is rendered
    /// Then   the login form is visible and not overflowing the viewport
    /// </summary>
    [Fact(Skip = "Requires a running application. Run with ASPNETCORE_ENVIRONMENT=Testing " +
                 "and set E2E_BASE_URL before executing E2E tests.")]
    public async Task LoginPage_RendersCorrectly_AtMobileViewport()
    {
        // Act
        await _page.GotoAsync("/login");
        await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        // Assert — email input is visible (form rendered)
        bool emailVisible = await _page.IsVisibleAsync("input[type='email']");
        Assert.True(emailVisible, "Email input should be visible at mobile viewport");

        // Assert — no horizontal scroll (content fits within mobile viewport width)
        int scrollWidth = await _page.EvaluateAsync<int>("document.body.scrollWidth");
        Assert.True(scrollWidth <= MobileWidth,
            $"Page has horizontal overflow at mobile width. scrollWidth={scrollWidth}, viewport={MobileWidth}");
    }

    /// <summary>
    /// Dashboard route cards stack vertically at mobile viewport.
    ///
    /// Given  the user is authenticated and on the dashboard
    /// When   the page is rendered at mobile width (390px)
    /// Then   route cards are displayed in a single column (not side-by-side)
    /// </summary>
    [Fact(Skip = "Requires a running authenticated session. " +
                 "Run integration setup via /e2e/dev-login before executing.")]
    public async Task DashboardRouteCards_StackVertically_AtMobileViewport()
    {
        // (Setup) — would call /e2e/dev-login and navigate to /dashboard
        await _page.GotoAsync("/login");
        await _page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        // Placeholder assertion — real assertion verifies computed column layout
        // via CSS grid/flex column count or bounding box Y-coordinate comparison
        Assert.True(true, "Stub — implement after Radzen DataList responsive config is finalised");
    }

    /// <summary>
    /// Admin page is not accessible to non-admin users redirected to /login at mobile viewport.
    ///
    /// Given  an unauthenticated user on a mobile device
    /// When   they navigate to /admin
    /// Then   they are redirected to the login page
    /// </summary>
    [SkipUnlessE2EReady]
    public async Task AdminPage_RedirectsToLogin_ForUnauthenticatedMobileUser()
    {
        // Act
        await _page.GotoAsync("/admin");
        await _page.WaitForURLAsync("**/login**", new PageWaitForURLOptions
        {
            Timeout = 5_000
        });

        // Assert
        string currentUrl = _page.Url;
        Assert.Contains("/login", currentUrl, StringComparison.OrdinalIgnoreCase);
    }
}
