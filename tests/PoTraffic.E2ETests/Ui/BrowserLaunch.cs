using Microsoft.Playwright;

namespace PoTraffic.E2ETests.Ui;

/// <summary>
/// Launch mode for the Playwright browser: headed Chrome unless <c>E2E_HEADED</c> is 0/false
/// (CI sets it to 0; dev workstations leave it unset).
/// </summary>
public static class BrowserLaunch
{
    public static BrowserTypeLaunchOptions ResolveLaunchOptions()
    {
        string? env = Environment.GetEnvironmentVariable("E2E_HEADED");
        bool headed = env is null || env == "1" || env.Equals("true", StringComparison.OrdinalIgnoreCase);
        return new BrowserTypeLaunchOptions
        {
            Headless = !headed,
            Channel = "chrome",  // use the system Chrome, not bundled Chromium, when headed
            SlowMo = headed ? 0 : 50,   // slow down slightly on headed runs so screenshots look natural
            Args = ["--no-sandbox", "--disable-setuid-sandbox", "--disable-dev-shm-usage"]
        };
    }
}
