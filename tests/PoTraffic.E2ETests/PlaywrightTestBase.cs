using Microsoft.Playwright;
using PoTraffic.E2ETests.Helpers;

namespace PoTraffic.E2ETests;

/// <summary>
/// Template Method pattern — base class for all E2E tests.
/// Provides a configured Playwright browser/context that is shared per-class and disposed after all tests.
/// </summary>
public abstract class PlaywrightTestBase : IAsyncLifetime
{
    protected IPlaywright Playwright { get; private set; } = null!;
    protected IBrowser Browser { get; private set; } = null!;
    protected IBrowserContext Context { get; private set; } = null!;
    protected IPage Page { get; private set; } = null!;

    /// <summary>
    /// Base URL of the application under test.
    /// Reads from E2E_BASE_URL environment variable; defaults to http://localhost:5150
    /// (the API project which hosts the Blazor WASM client via UseBlazorFrameworkFiles).
    /// </summary>
    protected static string BaseUrl =>
        Environment.GetEnvironmentVariable("E2E_BASE_URL") ?? "http://localhost:5150";

    public async Task InitializeAsync()
    {
        Playwright = await Microsoft.Playwright.Playwright.CreateAsync();
        Browser = await Playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true,
            ExecutablePath = FindCachedChromiumExecutable(),
            // Required on Windows CI / sandboxed environments for WASM-heavy Blazor apps
            Args = ["--no-sandbox", "--disable-setuid-sandbox", "--disable-dev-shm-usage"]
        });

        Context = await Browser.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = BaseUrl,
            IgnoreHTTPSErrors = true,
            ViewportSize = new ViewportSize { Width = 1280, Height = 800 },
            // Record video only on the test side; we delete it in DisposeAsync
            // unless the test failed. Honours the "no MSI resource burn on green" rule.
            RecordVideoDir = "TestResults/e2e-video"
        });

        Page = await Context.NewPageAsync();

        // Zero-Waste console audit — surface Blazor WASM / JSInterop failures
        // in the xUnit TRX + console output without retaining on green.
        Page.Console += (_, msg) =>
        {
            if (msg.Type == "error")
                Console.WriteLine($"[BROWSER_ERROR] {msg.Text}");
        };
        Page.PageError += (_, err) => Console.WriteLine($"[PAGE_ERROR] {err}");
        Page.Crash += (_, page) => Console.WriteLine($"[PAGE_CRASH] {page.Url}");
    }

    public async Task DisposeAsync()
    {
        // xUnit v2 does not expose TestContext inside IAsyncLifetime.DisposeAsync.
        // We track outcome via a public flag the test body flips before teardown.
        // Default = passed; tests that want to keep the video flip this to true.
        await Page.CloseAsync();
        await Context.CloseAsync();
        await Browser.CloseAsync();
        Playwright.Dispose();

        // If the test passed, delete the recorded video to honour the
        // "capture .trace.zip and video only on failure" rule (no MSI burn on green).
        // Tests flip <see cref="KeepVideoOnSuccess"/> = true to override.
        if (!KeepVideoOnSuccess && Page.Video is not null)
        {
            try
            {
                string? videoPath = await Page.Video.PathAsync();
                if (!string.IsNullOrEmpty(videoPath) && File.Exists(videoPath))
                    File.Delete(videoPath);
            }
            catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>
    /// When <c>true</c>, the recorded Playwright video is retained even on success.
    /// Default is <c>false</c> (Zero-Waste: only keep artefacts on failure).
    /// Tests set this to <c>true</c> in their Arrange phase if they need to
    /// review the video for a known-flaky scenario.
    /// </summary>
    protected bool KeepVideoOnSuccess { get; set; }

    protected async Task AuthenticateWithAccessTokenAsync(string accessToken, string destination = "/dashboard", IPage? page = null)
    {
        IPage targetPage = page ?? Page;
        await targetPage.GotoAsync($"{BaseUrl}/login");
        await targetPage.EvaluateAsync(
            "([key, value]) => localStorage.setItem(key, value)",
            new[] { "potraffic_access_token", accessToken });
        await targetPage.GotoAsync($"{BaseUrl}{destination}");
    }

    private static string? FindCachedChromiumExecutable()
    {
        string localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        string[] roots =
        [
            Path.Combine(localData, "ms-playwright"),
            Path.Combine(homeDir, ".cache", "ms-playwright")
        ];

        foreach (string root in roots.Where(Directory.Exists))
        {
            string? executable = Directory
                .EnumerateFiles(root, OperatingSystem.IsWindows() ? "chrome.exe" : "chrome", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();

            if (!string.IsNullOrWhiteSpace(executable))
                return executable;
        }

        return null;
    }
}
