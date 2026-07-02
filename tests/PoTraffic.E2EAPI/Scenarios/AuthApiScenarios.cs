using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using PoTraffic.Shared.DTOs.Auth;

namespace PoTraffic.E2EAPI.Scenarios;

/// <summary>Pure API coverage of the BFF cookie session lifecycle (no browser).</summary>
public sealed class AuthApiScenarios
{
    [SkipUnlessApiReady]
    public async Task Health_ReturnsHealthyJson()
    {
        using HttpClient client = ApiSessionFactory.CreateAnonymous();
        HttpResponseMessage resp = await client.GetAsync("/health");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        string body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("\"status\"").And.Contain("\"entries\"");
    }

    [SkipUnlessApiReady]
    public async Task GuestSession_MeReflectsCookie_LogoutInvalidates()
    {
        (HttpClient client, AuthMeResponse me) = await ApiSessionFactory.CreateGuestSessionAsync();
        using HttpClient disposer = client;

        AuthMeResponse? current = await client.GetFromJsonAsync<AuthMeResponse>("/api/auth/me");
        current!.UserId.Should().Be(me.UserId);

        (await client.PostAsync("/api/auth/logout", content: null)).StatusCode
            .Should().Be(HttpStatusCode.NoContent);
        (await client.GetAsync("/api/auth/me")).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized);
    }

    [SkipUnlessApiReady]
    public async Task ProtectedEndpoints_Require_Session()
    {
        using HttpClient client = ApiSessionFactory.CreateAnonymous();

        (await client.GetAsync("/api/routes")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await client.GetAsync("/api/account/quota")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await client.GetAsync("/api/admin/users")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
