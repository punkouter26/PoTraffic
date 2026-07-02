// filepath: tests/PoTraffic.Tests.E2E/Api/ApiSessionFactory.cs
using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace PoTraffic.Tests.E2E.Api;

/// <summary>
/// Creates cookie-authenticated <see cref="HttpClient"/> instances against the
/// live PoTraffic.Api running in the Testing environment (default base URL
/// <c>http://localhost:5150</c>, override with <c>E2E_BASE_URL</c>).
///
/// Mirrors the BFF cookie behaviour the Blazor WASM client uses in-browser —
/// every request flows through the same .PoTraffic.Auth cookie jar.
/// </summary>
public static class ApiSessionFactory
{
    public const string DefaultBaseUrl = "http://localhost:5150";
    public const string DevelopmentBaseUrl = "http://localhost:5000";

    /// <summary>
    /// Resolves the live base URL. Honours <c>E2E_BASE_URL</c> first, then probes
    /// the standard Testing (5150) and Development (5000) ports — first one
    /// that answers /health wins. Probes fresh on every call so a dev that
    /// boots the API after test discovery still hits it.
    /// </summary>
    public static string BaseUrl
    {
        get
        {
            string? envOverride = Environment.GetEnvironmentVariable("E2E_BASE_URL");
            if (!string.IsNullOrWhiteSpace(envOverride)) return envOverride;
            return ProbeOrDefault(DefaultBaseUrl, DevelopmentBaseUrl) ?? DefaultBaseUrl;
        }
    }

    private static string? ProbeOrDefault(params string[] candidates)
    {
        foreach (string candidate in candidates)
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
                // fall through to next candidate
            }
        }
        return null;
    }

    /// <summary>Anonymous client (no cookie).</summary>
    public static HttpClient CreateAnonymous() => new() { BaseAddress = new Uri(BaseUrl) };

    /// <summary>
    /// Creates a Guest-authenticated client. A new unique GUEST account is
    /// minted per call so parallel runs never collide. Returns the client
    /// (already signed in) and the email of the new account (for diagnostics).
    /// </summary>
    public static async Task<(HttpClient Client, string Email)> CreateGuestSessionAsync()
    {
        // Use a default cookie container so .PoTraffic.Auth set on /guest-login
        // flows automatically into every subsequent request.
        var handler = new HttpClientHandler { UseCookies = true };
        var client = new HttpClient(handler) { BaseAddress = new Uri(BaseUrl) };

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/auth/guest-login", new { });
        response.EnsureSuccessStatusCode();

        GuestLoginResponse? body = await response.Content
            .ReadFromJsonAsync(ApiJsonContext.Default.GuestLoginResponse, CancellationToken.None);

        if (body is null || string.IsNullOrWhiteSpace(body.Email))
            throw new InvalidOperationException("guest-login endpoint returned no email.");

        return (client, body.Email);
    }

    public sealed record GuestLoginResponse(
        [property: JsonPropertyName("email")] string Email);
}