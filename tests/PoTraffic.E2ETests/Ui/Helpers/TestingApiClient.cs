// filepath: tests/PoTraffic.E2ETests/Ui/Helpers/TestingApiClient.cs
using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace PoTraffic.E2ETests.Ui.Helpers;

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

    /// <summary>
    /// Promotes a deterministic admin user via the testing-only endpoint.
    ///
    /// <para>
    /// Must be a POST: <c>TestingEndpoints</c> maps <c>/e2e/seed-admin</c> with
    /// <c>MapPost</c>. A GET does not match the route, falls through to the SPA
    /// fallback, and returns <c>index.html</c> — which surfaced as the opaque
    /// "'&lt;' is an invalid start of a value" JSON error that failed every scenario
    /// needing an admin session.
    /// </para>
    /// </summary>
    public async Task<string> SeedAdminAsync(CancellationToken ct = default)
    {
        using HttpResponseMessage result = await _http.PostAsync("/e2e/seed-admin", content: null, ct);
        result.EnsureSuccessStatusCode();

        SeedAdminResponse? response =
            await result.Content.ReadFromJsonAsync<SeedAdminResponse>(cancellationToken: ct);
        if (response is null || string.IsNullOrWhiteSpace(response.Email))
            throw new InvalidOperationException("seed-admin endpoint returned no email.");
        return response.Email;
    }

    /// <summary>
    /// Issues a BFF cookie for the given email + role via the dev-login endpoint and
    /// returns the signed-in email, or null when the call failed.
    ///
    /// <para>
    /// The cookie lands in this client's handler, not in the Playwright browser — UI
    /// tests still authenticate the page through
    /// <c>PlaywrightTestBase.AuthenticateViaDevLoginAsync</c>. Callers use this purely
    /// as a precondition check that the account resolves.
    /// </para>
    /// </summary>
    public async Task<string?> DevLoginAsync(string email, string role = "Commuter", CancellationToken ct = default)
    {
        using HttpResponseMessage response = await _http.PostAsJsonAsync(
            "/e2e/dev-login",
            new DevLoginRequest(email, role),
            ct);
        if (!response.IsSuccessStatusCode)
            return null;

        DevLoginResponse? body = await response.Content.ReadFromJsonAsync<DevLoginResponse>(cancellationToken: ct);
        return body?.Email;
    }

    /// <summary>
    /// Seeds a route for the given user and returns its id and the two addresses as
    /// stored, which is what callers match route cards on.
    /// </summary>
    public async Task<(RouteId RouteId, string OriginAddress, string DestinationAddress)> SeedRouteAsync(
        string email,
        string originAddress,
        string destinationAddress,
        CancellationToken ct = default)
    {
        using HttpResponseMessage response = await _http.PostAsJsonAsync(
            "/e2e/seed-route",
            new SeedRouteRequest(email, originAddress, destinationAddress),
            ct);
        response.EnsureSuccessStatusCode();

        SeedRouteResponse? body = await response.Content.ReadFromJsonAsync<SeedRouteResponse>(cancellationToken: ct);
        if (body is null || body.RouteId.IsEmpty)
            throw new InvalidOperationException("seed-route endpoint did not return a RouteId.");
        return (body.RouteId, body.OriginAddress, body.DestinationAddress);
    }

    private sealed record SeedAdminResponse(
        [property: JsonPropertyName("email")] string Email);

    private sealed record DevLoginRequest(
        [property: JsonPropertyName("email")] string Email,
        [property: JsonPropertyName("role")] string Role);

    /// <summary>
    /// Mirrors the <c>AuthMeResponse</c> that <c>/e2e/dev-login</c> actually returns.
    /// It previously declared a <c>sessionId</c> the endpoint has never sent, so the
    /// property deserialised to null on every successful call and the helper reported
    /// failure on HTTP 200.
    /// </summary>
    private sealed record DevLoginResponse(
        [property: JsonPropertyName("userId")] string? UserId,
        [property: JsonPropertyName("email")] string? Email,
        [property: JsonPropertyName("role")] string? Role,
        [property: JsonPropertyName("authProvider")] string? AuthProvider);

    /// <summary>
    /// Mirrors the request <c>/e2e/seed-route</c> binds. The names previously sent
    /// (<c>email</c>/<c>origin</c>/<c>destination</c>) matched none of the endpoint's
    /// parameters, so every field bound to null and the handler rejected the call with
    /// "User 'null' not found" — a 404 that read as a missing route rather than a
    /// malformed body. <c>Provider</c> is required too, and defaults to Google Maps (0).
    /// </summary>
    private sealed record SeedRouteRequest(
        [property: JsonPropertyName("userEmail")] string UserEmail,
        [property: JsonPropertyName("originAddress")] string OriginAddress,
        [property: JsonPropertyName("destinationAddress")] string DestinationAddress,
        [property: JsonPropertyName("provider")] int Provider = 0);

    /// <summary>
    /// Mirrors what the endpoint returns: the seeded route and its two addresses. The
    /// previous <c>originCoords</c>/<c>destCoords</c> names matched nothing on the wire
    /// and deserialised to null.
    /// </summary>
    private sealed record SeedRouteResponse(
        [property: JsonPropertyName("routeId")] RouteId RouteId,
        [property: JsonPropertyName("originAddress")] string OriginAddress,
        [property: JsonPropertyName("destinationAddress")] string DestinationAddress);
}
