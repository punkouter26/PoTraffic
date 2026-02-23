using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using PoTraffic.IntegrationTests.Helpers;
using PoTraffic.Shared.DTOs.Auth;

namespace PoTraffic.IntegrationTests.Features.Auth;

/// <summary>
/// Integration tests for the Auth slice (Register, Login, RefreshToken).
/// FR-013: email uniqueness; JWT issuance; refresh token rotation.
/// </summary>
public sealed class AuthIntegrationTests : BaseIntegrationTest
{
    [SkipUnlessDockerAvailable]
    public async Task Register_CreatesUser_Login_ReturnsJwt_Refresh_ReturnsNewToken()
    {
        await ApplyMigrationsAsync();
        HttpClient client = CreateClient();

        // Act 1 — Register
        var registerBody = new { Email = "auth-flow@test.invalid", Password = "Str0ng!Pass", Locale = "en-IE" };
        HttpResponseMessage registerResponse = await client.PostAsJsonAsync("/api/auth/register", registerBody);
        registerResponse.StatusCode.Should().Be(HttpStatusCode.Created, "registration must succeed");

        AuthResponse? registerAuth = await registerResponse.Content.ReadFromJsonAsync<AuthResponse>();
        registerAuth.Should().NotBeNull();
        registerAuth!.AccessToken.Should().NotBeNullOrWhiteSpace("register should return an access token");
        registerAuth.RefreshToken.Should().NotBeNullOrWhiteSpace("register should return a refresh token");

        // Act 2 — Login with same credentials
        var loginBody = new { Email = "auth-flow@test.invalid", Password = "Str0ng!Pass" };
        HttpResponseMessage loginResponse = await client.PostAsJsonAsync("/api/auth/login", loginBody);
        loginResponse.StatusCode.Should().Be(HttpStatusCode.OK, "login with valid credentials must return 200");

        AuthResponse? loginAuth = await loginResponse.Content.ReadFromJsonAsync<AuthResponse>();
        loginAuth.Should().NotBeNull();
        loginAuth!.AccessToken.Should().NotBeNullOrWhiteSpace("login should return an access token");

        // Act 3 — Refresh token rotation
        HttpResponseMessage refreshResponse = await client.PostAsJsonAsync(
            "/api/auth/refresh-token",
            new { AccessToken = loginAuth.AccessToken, RefreshToken = loginAuth.RefreshToken });
        refreshResponse.StatusCode.Should().Be(HttpStatusCode.OK, "refresh token rotation must succeed");

        AuthResponse? refreshAuth = await refreshResponse.Content.ReadFromJsonAsync<AuthResponse>();
        refreshAuth.Should().NotBeNull();
        refreshAuth!.AccessToken.Should().NotBe(loginAuth.AccessToken, "new access token must differ from old");
    }

    [SkipUnlessDockerAvailable]
    public async Task Register_DuplicateEmail_Returns409()
    {
        await ApplyMigrationsAsync();
        HttpClient client = CreateClient();

        var body = new { Email = "dupe@test.invalid", Password = "Str0ng!Pass", Locale = "en-IE" };
        HttpResponseMessage first = await client.PostAsJsonAsync("/api/auth/register", body);
        first.StatusCode.Should().Be(HttpStatusCode.Created);

        HttpResponseMessage second = await client.PostAsJsonAsync("/api/auth/register", body);
        second.StatusCode.Should().Be(HttpStatusCode.Conflict,
            "FR-013: duplicate email registration must be rejected with 409");
    }

    [SkipUnlessDockerAvailable]
    public async Task Login_InvalidCredentials_Returns401()
    {
        await ApplyMigrationsAsync();
        HttpClient client = CreateClient();

        var registerBody = new { Email = "badlogin@test.invalid", Password = "Str0ng!Pass", Locale = "en-IE" };
        await client.PostAsJsonAsync("/api/auth/register", registerBody);

        var loginBody = new { Email = "badlogin@test.invalid", Password = "Wr0ng!Pass" };
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/auth/login", loginBody);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "invalid credentials must return 401");
    }
}
