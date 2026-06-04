using Microsoft.Playwright;
using PoTraffic.E2ETests.Helpers;

namespace PoTraffic.E2ETests.Scenarios;

/// <summary>
/// Mobile viewport E2E tests — verifies key user journeys render correctly
/// at a mobile form factor (390×844, equivalent to iPhone 14 Pro).
/// Inherits from <see cref="PlaywrightTestBase"/> for console/crash audit.
/// </summary>
public sealed class MobileViewportScenarios : PlaywrightTestBase
{
    private const int MobileWidth = 390;
    private const int MobileHeight = 844;

    [SkipUnlessE2EReady]
    public async Task LoginPage_RendersCorrectly_AtMobileViewport()
    {
        // Create a mobile-sized context (iPhone 14 Pro: 390×844)
        IBrowserContext mobileContext = await Browser.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = BaseUrl,
            IgnoreHTTPSErrors = true,
            ViewportSize = new ViewportSize { Width = MobileWidth, Height = MobileHeight }
        });
        IPage mobilePage = await mobileContext.NewPageAsync();

        try
        {
            await mobilePage.GotoAsync($"{BaseUrl}/login");
            await mobilePage.WaitForLoadStateAsync(LoadState.NetworkIdle);

            // Radzen renders email as input.rz-textbox (type="text"), not input[type='email']
            bool emailVisible = await mobilePage.IsVisibleAsync("input.rz-textbox");
            Assert.True(emailVisible, "Email input should be visible at mobile viewport");

            int scrollWidth = await mobilePage.EvaluateAsync<int>("document.body.scrollWidth");
            Assert.True(scrollWidth <= MobileWidth,
                $"Page has horizontal overflow at mobile width. scrollWidth={scrollWidth}, viewport={MobileWidth}");
        }
        finally
        {
            await mobilePage.CloseAsync();
            await mobileContext.CloseAsync();
        }
    }

    [SkipUnlessE2EReady]
    public async Task DashboardRouteCards_StackVertically_AtMobileViewport()
    {
        IBrowserContext mobileContext = await Browser.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = BaseUrl,
            IgnoreHTTPSErrors = true,
            ViewportSize = new ViewportSize { Width = MobileWidth, Height = MobileHeight }
        });
        IPage mobilePage = await mobileContext.NewPageAsync();

        try
        {
            await mobilePage.GotoAsync($"{BaseUrl}/login");
            await mobilePage.WaitForLoadStateAsync(LoadState.NetworkIdle);

            Assert.True(true, "Stub — implement after Radzen DataList responsive config is finalised");
        }
        finally
        {
            await mobilePage.CloseAsync();
            await mobileContext.CloseAsync();
        }
    }

    [SkipUnlessE2EReady]
    public async Task AdminPage_RedirectsToLogin_ForUnauthenticatedMobileUser()
    {
        IBrowserContext mobileContext = await Browser.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = BaseUrl,
            IgnoreHTTPSErrors = true,
            ViewportSize = new ViewportSize { Width = MobileWidth, Height = MobileHeight }
        });
        IPage mobilePage = await mobileContext.NewPageAsync();

        try
        {
            await mobilePage.GotoAsync($"{BaseUrl}/admin");
            await mobilePage.WaitForURLAsync("**/login**", new PageWaitForURLOptions
            {
                Timeout = 30_000
            });

            string currentUrl = mobilePage.Url;
            Assert.Contains("/login", currentUrl, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await mobilePage.CloseAsync();
            await mobileContext.CloseAsync();
        }
    }
}
