using Xunit;

namespace PoTraffic.E2ETests.Helpers;

/// <summary>
/// Conditional Fact — runs the E2E test when both the Playwright Chromium binaries
/// and a reachable application (at <c>E2E_BASE_URL</c>) are detected; skips gracefully otherwise.
/// Replaces the static <c>[Fact(Skip = "Requires running app + Playwright...")]</c> pattern
/// so CI can execute E2E tests once the environment is provisioned.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class SkipUnlessE2EReadyAttribute : FactAttribute
{
    // Matches PlaywrightTestBase.BaseUrl — API hosts the WASM client via UseBlazorFrameworkFiles
    private static readonly string BaseUrl =
        Environment.GetEnvironmentVariable("E2E_BASE_URL") ?? "http://localhost:5150";

    public SkipUnlessE2EReadyAttribute()
    {
        if (!IsPlaywrightInstalled())
        {
            Skip = "Playwright browser binaries not found — run 'dotnet tool run playwright install chromium' and set E2E_BASE_URL.";
            return;
        }

        if (!IsAppReachable())
        {
            Skip = $"App not reachable at {BaseUrl} — start API + Blazor WASM with ASPNETCORE_ENVIRONMENT=Testing and set E2E_BASE_URL.";
            return;
        }

        if (!HasTestingEndpoints())
        {
            Skip = $"Testing endpoints are unavailable at {BaseUrl} — ensure ASPNETCORE_ENVIRONMENT=Testing and use a host exposing POST /e2e/seed-admin.";
        }
    }

    private static bool IsPlaywrightInstalled()
    {
        // Playwright stores browser executables in %LOCALAPPDATA%/ms-playwright on Windows,
        // and ~/.cache/ms-playwright on Linux/macOS.
        string localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string homeDir   = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        string[] candidates =
        [
            Path.Combine(localData, "ms-playwright"),                  // Windows
            Path.Combine(homeDir, ".cache", "ms-playwright"),          // Linux / macOS
        ];

        return candidates.Any(dir => Directory.Exists(dir) && Directory.GetDirectories(dir).Length > 0);
    }

    private static bool IsAppReachable()
    {
        try
        {
            using var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (_, _, _, _) => true
            };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(3) };
            HttpResponseMessage response = client.GetAsync(BaseUrl).GetAwaiter().GetResult();
            return (int)response.StatusCode < 500;
        }
        catch
        {
            return false;
        }
    }

    private static bool HasTestingEndpoints()
    {
        try
        {
            using var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (_, _, _, _) => true
            };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };

            using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl.TrimEnd('/')}/e2e/seed-admin")
            {
                Content = new StringContent(string.Empty)
            };

            HttpResponseMessage response = client.Send(request);

            // Expected when test endpoints are mapped in Testing env.
            if (response.IsSuccessStatusCode)
                return true;

            // Typical non-testing responses (SPA fallback / endpoint missing / method not allowed).
            if (response.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.MethodNotAllowed)
                return false;

            // Other statuses (401/403/500) indicate endpoint path exists, so allow tests to run.
            return true;
        }
        catch
        {
            return false;
        }
    }
}
