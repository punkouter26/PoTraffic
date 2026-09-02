using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.WebUtilities;
using FluentAssertions;
using PoTraffic.IntegrationTests.Helpers;
using PoTraffic.Shared.DTOs.Auth;

namespace PoTraffic.IntegrationTests.Features.Auth;

/// <summary>
/// Integration tests for the BFF cookie auth slice (Testing guest-login, /me,
/// logout, external Microsoft OAuth). Email/password register + login were
/// removed — Microsoft OAuth is the only normal sign-in path.
/// </summary>
public sealed class AuthIntegrationTests : BaseIntegrationTest
{
    [SkipUnlessAzuriteAvailable]
    public async Task GuestLogin_EstablishesCookieSession_MeAndLogoutWork()
    {
        await ApplyMigrationsAsync();
        HttpClient client = CreateClient();

        // Act 1 — Guest login creates a real User row and sets the session cookie.
        HttpResponseMessage guestResponse = await client.PostAsync("/api/auth/guest-login", content: null);
        guestResponse.StatusCode.Should().Be(HttpStatusCode.OK, "guest login must succeed in the Testing environment");

        AuthMeResponse? guestAuth = await guestResponse.Content.ReadFromJsonAsync<AuthMeResponse>();
        guestAuth.Should().NotBeNull();
        guestAuth!.Email.Should().StartWith("guest").And.EndWith("@potraffic.dev");
        guestResponse.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? cookies).Should().BeTrue();
        cookies!.Should().Contain(c => c.StartsWith(".PoTraffic.Auth="), "BFF session must be an HttpOnly cookie");

        // Act 2 — /me reflects the cookie session (the factory client persists cookies).
        AuthMeResponse? me = await client.GetFromJsonAsync<AuthMeResponse>("/api/auth/me");
        me.Should().NotBeNull();
        me!.UserId.Should().Be(guestAuth.UserId);
        me.Role.Should().Be("Guest");

        // Act 3 — logout kills the session; /me now returns 200 with the empty-UserId
        // anonymous sentinel (the probe deliberately avoids a 401 so the WASM client
        // doesn't log a console error on first paint — see AuthEndpoints.Me).
        HttpResponseMessage logoutResponse = await client.PostAsync("/api/auth/logout", content: null);
        logoutResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        HttpResponseMessage meAfterLogout = await client.GetAsync("/api/auth/me");
        meAfterLogout.StatusCode.Should().Be(HttpStatusCode.OK);
        AuthMeResponse? meAnon = await meAfterLogout.Content.ReadFromJsonAsync<AuthMeResponse>();
        meAnon.Should().NotBeNull();
        // UserId.Empty, not Guid.Empty — UserId is a distinct type, so comparing it against a
        // raw Guid fails even when the value is right (Should().Be takes object, so the
        // mismatch is invisible at compile time).
        meAnon!.UserId.Should().Be(UserId.Empty,
            "the session cookie must be invalidated by logout (anonymous sentinel)");
    }

    [SkipUnlessAzuriteAvailable]
    public async Task ExternalMicrosoftLogin_StartAndCallback_SignsInAndRedirectsToReturnUrl()
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

        // BFF: success sets the session cookie and lands directly on the return URL —
        // no tokens in the redirect target.
        callbackResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
        callbackResponse.Headers.Location!.ToString().Should().Be("/dashboard");
        callbackResponse.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? cookies).Should().BeTrue();
        cookies!.Should().Contain(c => c.StartsWith(".PoTraffic.Auth="), "callback must establish the cookie session");
    }

    [SkipUnlessAzuriteAvailable]
    public async Task ExternalCallback_WithInvalidState_RedirectsToLoginWithError()
    {
        await ApplyMigrationsAsync();
        HttpClient client = CreateClientNoRedirect();

        HttpResponseMessage callbackResponse = await client.GetAsync(
            "/api/auth/external/microsoft/callback?code=integration-test-code&state=tampered");

        callbackResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
        callbackResponse.Headers.Location!.ToString().Should().StartWith("/login?error=");
    }
}
