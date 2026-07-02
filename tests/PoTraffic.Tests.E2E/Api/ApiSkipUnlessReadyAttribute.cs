// filepath: tests/PoTraffic.Tests.E2E/Api/ApiSkipUnlessReadyAttribute.cs
using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace PoTraffic.Tests.E2E.Api;

/// <summary>
/// xunit <see cref="FactAttribute"/> that auto-skips when the live PoTraffic.Api
/// (any environment) is not reachable. Lets CI pass on machines without a
/// running instance while still letting a local dev drive the suite.
///
/// Probes the standard Testing (5150) and Development (5000) ports so the
/// scenario file does not need to know which profile is running.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class ApiSkipUnlessReadyAttribute : FactAttribute
{
    private static readonly string[] CandidatePorts =
        [ApiSessionFactory.DefaultBaseUrl, ApiSessionFactory.DevelopmentBaseUrl];

    public override string? Skip => GetReachableBaseUrl() is null
        ? $"PoTraffic.Api /health unreachable on {string.Join(", ", CandidatePorts)}"
        : null;

    /// <summary>Returns the first base URL whose /health responds 200, or null.</summary>
    public static string? GetReachableBaseUrl()
    {
        foreach (string candidate in CandidatePorts)
        {
            try
            {
                using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
                using var resp = probe.GetAsync(new Uri(new Uri(candidate), "/health"))
                                      .GetAwaiter().GetResult();
                if (resp.IsSuccessStatusCode) return candidate;
            }
            catch
            {
                // try the next candidate
            }
        }
        return null;
    }

    /// <summary>
    /// Eagerly probes the live API and throws if not reachable, so xunit skips
    /// the test cleanly without retrying the probe inside each assertion.
    /// </summary>
    public static async Task ThrowUnlessReadyAsync()
    {
        string? baseUrl = null;
        Exception? lastError = null;
        foreach (string candidate in CandidatePorts)
        {
            try
            {
                using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
                using var resp = await probe.GetAsync(new Uri(new Uri(candidate), "/health"));
                if (resp.IsSuccessStatusCode)
                {
                    baseUrl = candidate;
                    break;
                }
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        if (baseUrl is null)
            throw new InvalidOperationException(
                $"PoTraffic.Api /health unreachable on {string.Join(", ", CandidatePorts)}: {lastError?.Message ?? "no response"}");
    }
}