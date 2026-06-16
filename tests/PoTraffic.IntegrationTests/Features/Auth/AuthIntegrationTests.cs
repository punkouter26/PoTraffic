using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.WebUtilities;
using FluentAssertions;
using PoTraffic.IntegrationTests.Helpers;
using PoTraffic.Shared.DTOs.Auth;

namespace PoTraffic.IntegrationTests.Features.Auth;

/// <summary>
/// Integration tests for the surviving Auth slice (Testing guest-login, refresh-token, external Microsoft OAuth).
/// Email/password register + login were removed — Microsoft OAuth is the only normal sign-in path.
/// </summary>
public sealed class AuthIntegrationTests : BaseIntegrationTest
{
    [SkipUnlessAzuriteAvailable]
    public async Task GuestLogin_ReturnsJwt_Refresh_ReturnsNewToken()
    {
        await ApplyMigrationsAsync();
        HttpClient client = CreateClient();

        // Act 1 — Guest login creates a real User row and returns tokens.
        HttpResponseMessage guestResponse = await client.PostAsync("/api/auth/guest-login", content: null);
        guestResponse.StatusCode.Should().Be(HttpStatusCode.OK, "guest login must succeed in the Testing environment");

        AuthResponse? guestAuth = await guestResponse.Content.ReadFromJsonAsync<AuthResponse>();
        guestAuth.Should().NotBeNull();
        guestAuth!.AccessToken.Should().NotBeNullOrWhiteSpace("guest login should return an access token");
        guestAuth.RefreshToken.Should().NotBeNullOrWhiteSpace("guest login should return a refresh token");

        // Act 2 — Refresh token rotation
        HttpResponseMessage refreshResponse = await client.PostAsJsonAsync(
            "/api/auth/refresh-token",
            new { AccessToken = guestAuth.AccessToken, RefreshToken = guestAuth.RefreshToken });
        refreshResponse.StatusCode.Should().Be(HttpStatusCode.OK, "refresh token rotation must succeed");

        AuthResponse? refreshAuth = await refreshResponse.Content.ReadFromJsonAsync<AuthResponse>();
        refreshAuth.Should().NotBeNull();
        refreshAuth!.AccessToken.Should().NotBe(guestAuth.AccessToken, "new access token must differ from old");
    }

    [SkipUnlessAzuriteAvailable]
    public async Task ExternalMicrosoftLogin_StartAndCallback_ReturnsCompletionRedirectWithAccessToken()
    {
        await ApplyMigrationsAsync();
        // Use a no-redirect client so we can inspect the 302 Location header directly.
        HttpClient client = CreateClientNoRedirect();

        HttpResponseMessage startResponse = await client.GetAsync("/api/auth/external/microsoft/start?returnUrl=/dashboard");
        startResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);

        Uri? startLocation = startResponse.Headers.Location;
        startLocation.Should().NotBeNull();

        Dictionary<string, string> query = QueryHelpers.ParseQuery(startLocation!.Query)
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value.ToString());

        query.TryGetValue("state", out string? state).Should().BeTrue();
        state.Should().NotBeNullOrWhiteSpace();

        HttpResponseMessage callbackResponse = await client.GetAsync(
            $"/api/auth/external/microsoft/callback?code=integration-test-code&state={Uri.EscapeDataString(state!)}");

        callbackResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
        Uri? callbackLocation = callbackResponse.Headers.Location;
        callbackLocation.Should().NotBeNull();
        callbackLocation!.ToString().Should().StartWith("/auth/external-complete#");
        callbackLocation.ToString().Should().Contain("accessToken=");
    }
}
