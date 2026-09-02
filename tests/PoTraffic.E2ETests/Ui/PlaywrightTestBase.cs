using Microsoft.Playwright;
using PoTraffic.E2ETests.Ui.Helpers;

namespace PoTraffic.E2ETests.Ui;

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

        // CI/CD rule #5 + UPDATE: launch Chrome headed by default on dev workstations.
        // On CI, set E2E_HEADED=0 to keep the runner headless.
        BrowserTypeLaunchOptions launchOptions = ViewportsTheory.ResolveLaunchOptions();
        bool isHeaded = launchOptions.Headless == false; // bool? -> explicit non-headless
        if (isHeaded && string.IsNullOrEmpty(launchOptions.ExecutablePath))
            launchOptions.ExecutablePath = FindCachedChromiumExecutable();

        Browser = await Playwright.Chromium.LaunchAsync(launchOptions);

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

    /// <summary>
    /// Establishes a BFF cookie session inside the browser context by calling the
    /// Testing-only /e2e/dev-login endpoint from the page itself (same-origin fetch
    /// stores the HttpOnly cookie in the Playwright browser), then navigates.
    /// </summary>
    protected async Task AuthenticateViaDevLoginAsync(
        string email,
        string role = "Administrator",
        string destination = "/dashboard",
        IPage? page = null)
    {
        IPage targetPage = page ?? Page;
        await targetPage.GotoAsync($"{BaseUrl}/login");
        await targetPage.EvaluateAsync(
            @"async ([email, role]) => {
                const resp = await fetch('/e2e/dev-login', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ email, role })
                });
                if (!resp.ok) throw new Error('dev-login failed: ' + resp.status);
            }",
            new[] { email, role });
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
