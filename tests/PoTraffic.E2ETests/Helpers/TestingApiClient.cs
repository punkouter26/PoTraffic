using System.Net.Http.Json;
using PoTraffic.Shared.DTOs.Auth;

namespace PoTraffic.E2ETests.Helpers;

/// <summary>
/// Typed HTTP client wrapping the testing-only endpoints: /e2e/dev-login, /e2e/seed, /e2e/seed-admin.
/// Used by all E2E scenarios to authenticate and seed test data.
/// Available in Development and Testing environments (never Production).
/// </summary>
public sealed class TestingApiClient
{
    private readonly HttpClient _http;

    public TestingApiClient(HttpClient http)
    {
        _http = http;
    }

    /// <summary>
    /// Calls POST /e2e/dev-login to obtain a pre-issued JWT for the given role.
    /// Returns null when the endpoint is not registered (Production environments).
    /// </summary>
    public async Task<string?> DevLoginAsync(string email, string role = "Commuter", CancellationToken ct = default)
    {
        HttpResponseMessage response = await _http.PostAsJsonAsync("/e2e/dev-login", new { email, role }, ct);
        if (!response.IsSuccessStatusCode) return null;
        var result = await response.Content.ReadFromJsonAsync<DevLoginResponse>(ct);
        return result?.Token;
    }

    /// <summary>
    /// Calls POST /e2e/seed to seed the database with test data.
    /// </summary>
    public async Task SeedAsync(object seedPayload, CancellationToken ct = default)
    {
        HttpResponseMessage response = await _http.PostAsJsonAsync("/e2e/seed", seedPayload, ct);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Calls POST /e2e/seed-admin to ensure a known Administrator user exists in the database.
    /// Idempotent — safe to call multiple times.
    /// Returns the seeded credentials (email + password).
    /// </summary>
    public async Task<(string Email, string Password)> SeedAdminAsync(CancellationToken ct = default)
    {
        HttpResponseMessage response = await _http.PostAsync("/e2e/seed-admin", content: null, ct);
        response.EnsureSuccessStatusCode();
        SeedAdminResponse? result = await response.Content.ReadFromJsonAsync<SeedAdminResponse>(ct);
        return (result!.Email, result.Password);
    }

    /// <summary>
    /// Calls POST /e2e/seed-route to create a route directly in the database for the given user.
    /// Bypasses geocoding — safe to use when the provider is a development stub.
    /// </summary>
    public async Task<(Guid RouteId, string OriginAddress, string DestinationAddress)> SeedRouteAsync(
        string userEmail,
        string originAddress,
        string destinationAddress,
        int provider = 0,
        CancellationToken ct = default)
    {
        HttpResponseMessage response = await _http.PostAsJsonAsync(
            "/e2e/seed-route",
            new { userEmail, originAddress, destinationAddress, provider },
            ct);
        response.EnsureSuccessStatusCode();
        SeedRouteResponse? result = await response.Content.ReadFromJsonAsync<SeedRouteResponse>(ct);
        return (result!.RouteId, result.OriginAddress, result.DestinationAddress);
    }

    private sealed record DevLoginResponse(string Token);
    private sealed record SeedAdminResponse(string Email, string Password);
    private sealed record SeedRouteResponse(Guid RouteId, string OriginAddress, string DestinationAddress);
}
