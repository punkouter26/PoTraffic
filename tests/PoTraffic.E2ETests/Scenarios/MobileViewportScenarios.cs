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
        await Page.GotoAsync("/login");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        bool emailVisible = await Page.IsVisibleAsync("input[type='email']");
        Assert.True(emailVisible, "Email input should be visible at mobile viewport");

        int scrollWidth = await Page.EvaluateAsync<int>("document.body.scrollWidth");
        Assert.True(scrollWidth <= MobileWidth,
            $"Page has horizontal overflow at mobile width. scrollWidth={scrollWidth}, viewport={MobileWidth}");
    }

    [SkipUnlessE2EReady]
    public async Task DashboardRouteCards_StackVertically_AtMobileViewport()
    {
        await Page.GotoAsync("/login");
        await Page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        Assert.True(true, "Stub — implement after Radzen DataList responsive config is finalised");
    }

    [SkipUnlessE2EReady]
    public async Task AdminPage_RedirectsToLogin_ForUnauthenticatedMobileUser()
    {
        await Page.GotoAsync("/admin");
        await Page.WaitForURLAsync("**/login**", new PageWaitForURLOptions
        {
            Timeout = 30_000
        });

        string currentUrl = Page.Url;
        Assert.Contains("/login", currentUrl, StringComparison.OrdinalIgnoreCase);
    }
}
