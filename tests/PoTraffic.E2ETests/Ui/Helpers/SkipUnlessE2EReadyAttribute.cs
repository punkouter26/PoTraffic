// filepath: tests/PoTraffic.E2ETests/Ui/Helpers/SkipUnlessE2EReadyAttribute.cs
using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace PoTraffic.E2ETests.Ui.Helpers;

/// <summary>
/// xUnit attribute that auto-skips an E2E scenario when:
/// <list type="bullet">
///   <item>Playwright Chromium binaries are not installed (e.g. CI w/o
///         <c>playwright install</c> step), or</item>
///   <item>the live API base URL (<c>E2E_BASE_URL</c>, default
///         <c>http://localhost:5150</c>) is unreachable.</item>
/// </list>
/// Lets CI pass on machines that have not bootstrapped the E2E environment.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class SkipUnlessE2EReadyAttribute : FactAttribute
{
    private const string DefaultBaseUrl = "http://localhost:5150";
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);

    public override string? Skip
    {
        get
        {
            string baseUrl = Environment.GetEnvironmentVariable("E2E_BASE_URL") ?? DefaultBaseUrl;
            if (!IsAppReachable(baseUrl))
                return $"PoTraffic.API not reachable at {baseUrl} (set E2E_BASE_URL to override; otherwise the live API must be running).";
            if (!IsPlaywrightInstalled())
                return "Playwright Chromium binaries are not installed (run 'playwright install chromium').";
            return null;
        }
    }

    private static bool IsAppReachable(string baseUrl)
    {
        try
        {
            using var handler = new HttpClientHandler();
            using var client = new HttpClient(handler) { Timeout = ProbeTimeout };
            using var response = client.GetAsync(new Uri(new Uri(baseUrl), "/health/json"), HttpCompletionOption.ResponseHeadersRead)
                                       .GetAwaiter().GetResult();
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsPlaywrightInstalled()
    {
        // The presence of the env var PLAYWRIGHT_BROWSERS_PATH or the well-known
        // cache dir is a sufficient signal — invoking the API here would require
        // static init of Playwright on the test thread.
        string? cachePath = Environment.GetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH");
        if (!string.IsNullOrEmpty(cachePath) && System.IO.Directory.Exists(cachePath))
            return true;

        string defaultCache = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ms-playwright");
        return System.IO.Directory.Exists(defaultCache);
    }
}
