// filepath: tests/PoTraffic.Tests.E2E/Ui/Helpers/TestingApiClient.cs
using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace PoTraffic.Tests.E2E.Helpers;

/// <summary>
/// Tiny client for the Testing-only endpoints that back the E2E scenarios
/// (<c>/e2e/seed-admin</c>, <c>/e2e/dev-login</c>, <c>/e2e/seed-route</c>).
/// Lets a Playwright test promote a deterministic email to Administrator,
/// obtain a BFF cookie session, and pre-seed a route — all without driving
/// the OAuth flow.
/// </summary>
public sealed class TestingApiClient
{
    private readonly HttpClient _http;

    public TestingApiClient(HttpClient http)
    {
        _http = http;
    }

    /// <summary>Promotes a deterministic admin user via the testing-only endpoint.</summary>
    public async Task<string> SeedAdminAsync(CancellationToken ct = default)
    {
        SeedAdminResponse? response = await _http.GetFromJsonAsync<SeedAdminResponse>("/e2e/seed-admin", ct);
        if (response is null || string.IsNullOrWhiteSpace(response.Email))
            throw new InvalidOperationException("seed-admin endpoint returned no email.");
        return response.Email;
    }

    /// <summary>Issues a BFF cookie for the given email + role via the dev-login endpoint.</summary>
    public async Task<string?> DevLoginAsync(string email, string role = "Commuter", CancellationToken ct = default)
    {
        HttpResponseMessage response = await _http.PostAsJsonAsync(
            "/e2e/dev-login",
            new DevLoginRequest(email, role),
            ct);
        if (!response.IsSuccessStatusCode)
            return null;
        DevLoginResponse? body = await response.Content.ReadFromJsonAsync<DevLoginResponse>(cancellationToken: ct);
        return body?.SessionId;
    }

    /// <summary>Seeds a route for the given user and returns (RouteId, OriginCoords, DestCoords).</summary>
    public async Task<(Guid RouteId, string OriginCoords, string DestCoords)> SeedRouteAsync(
        string email,
        string originAddress,
        string destinationAddress,
        CancellationToken ct = default)
    {
        HttpResponseMessage response = await _http.PostAsJsonAsync(
            "/e2e/seed-route",
            new SeedRouteRequest(email, originAddress, destinationAddress),
            ct);
        response.EnsureSuccessStatusCode();
        SeedRouteResponse? body = await response.Content.ReadFromJsonAsync<SeedRouteResponse>(cancellationToken: ct);
        if (body is null || body.RouteId == Guid.Empty)
            throw new InvalidOperationException("seed-route endpoint did not return a RouteId.");
        return (body.RouteId, body.OriginCoords, body.DestCoords);
    }

    private sealed record SeedAdminResponse(
        [property: JsonPropertyName("email")] string Email);

    private sealed record DevLoginRequest(
        [property: JsonPropertyName("email")] string Email,
        [property: JsonPropertyName("role")] string Role);

    private sealed record DevLoginResponse(
        [property: JsonPropertyName("sessionId")] string? SessionId,
        [property: JsonPropertyName("email")] string? Email);

    private sealed record SeedRouteRequest(
        [property: JsonPropertyName("email")] string Email,
        [property: JsonPropertyName("origin")] string Origin,
        [property: JsonPropertyName("destination")] string Destination);

    private sealed record SeedRouteResponse(
        [property: JsonPropertyName("routeId")] Guid RouteId,
        [property: JsonPropertyName("originCoords")] string OriginCoords,
        [property: JsonPropertyName("destCoords")] string DestCoords);
}
