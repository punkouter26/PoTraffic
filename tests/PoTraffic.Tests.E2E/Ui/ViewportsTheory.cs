// filepath: tests/PoTraffic.Tests.E2E/Ui/ViewportsTheory.cs
// CI/CD rule #5 + UPDATE: run every Playwright E2E test against both viewports
// AND launch headed Chrome when E2E_HEADED=1 (default true on dev workstations,
// false on CI).

using Microsoft.Playwright;

namespace PoTraffic.Tests.E2E;

/// <summary>
/// Helper that resolves the launch mode + viewport for an E2E test invocation.
/// Tests can call <see cref="ResolveLaunchOptions"/> inside their factory to
/// honour the E2E_HEADED env var (CI sets it to 0, dev workstations leave it
/// at the default of 1).
/// </summary>
public static class ViewportsTheory
{
    /// <summary>
    /// E2E_HEADED=0 → headless. E2E_HEADED=1 (or unset on dev) → headed Chrome.
    /// Default is <c>true</c> for the user's request "run e2e tests headed in chrome".
    /// </summary>
    public static bool HeadedByDefault => true;

    public static bool ShouldRunHeaded()
    {
        string? env = Environment.GetEnvironmentVariable("E2E_HEADED");
        if (env is null) return HeadedByDefault;
        return env == "1" || env.Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Produces xUnit <see cref="Xunit.TheoryData"/> entries — one per viewport —
    /// so a single <c>[Theory]</c> test method automatically runs against both
    /// mobile and desktop landscape.
    /// </summary>
    public static Xunit.TheoryData<ViewportProfile> All =>
        new()
        {
            ViewportProfile.Mobile,
            ViewportProfile.DesktopLandscape
        };

    public static BrowserTypeLaunchOptions ResolveLaunchOptions()
    {
        bool headed = ShouldRunHeaded();
        return new BrowserTypeLaunchOptions
        {
            Headless = !headed,
            Channel = "chrome",  // use the system Chrome, not bundled Chromium, when headed
            SlowMo = headed ? 0 : 50,   // slow down slightly on headed runs so screenshots look natural
            Args = ["--no-sandbox", "--disable-setuid-sandbox", "--disable-dev-shm-usage"]
        };
    }
}

// Trait decoration is opt-in via [Trait("viewport-matrix", "true")] on individual tests
// rather than a custom attribute, to avoid pulling in xunit.execution internals.