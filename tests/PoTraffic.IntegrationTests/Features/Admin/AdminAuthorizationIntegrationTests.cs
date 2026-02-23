using System.Net;
using System.Net.Http.Json;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PoTraffic.IntegrationTests.Helpers;
using PoTraffic.Api.Infrastructure.Security;

namespace PoTraffic.IntegrationTests.Features.Admin;

/// <summary>
/// Integration tests for the AdminOnly RBAC policy (FR-022).
/// Verifies that Commuter-role JWTs receive 403 Forbidden on all /api/admin/* endpoints,
/// and that Administrator-role JWTs receive 200 OK.
/// </summary>
public sealed class AdminAuthorizationIntegrationTests : BaseIntegrationTest
{
    // Template Method — set Testing environment so /e2e/dev-login bypass is registered.
    protected override void ConfigureHost(IWebHostBuilder builder) =>
        builder.UseEnvironment("Testing");

    [SkipUnlessDockerAvailable]
    public async Task AdminEndpoint_WithCommuterJwt_Returns403()
    {
        await ApplyMigrationsAsync();
        HttpClient client = CreateClient();

        // Obtain a commuter JWT via the Testing-only /e2e/dev-login endpoint (FR-022)
        HttpResponseMessage loginResponse = await client.PostAsJsonAsync(
            "/e2e/dev-login", new { Email = "commuter@test.invalid", Role = "Commuter" });
        loginResponse.StatusCode.Should().Be(HttpStatusCode.OK, "dev-login must succeed in Testing env");

        DevLoginResponse? dto = await loginResponse.Content.ReadFromJsonAsync<DevLoginResponse>();
        dto.Should().NotBeNull();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", dto!.Token);

        // Act — commuter JWT against admin endpoint
        HttpResponseMessage response = await client.GetAsync("/api/admin/users");

        // Assert — FR-022: non-admin receives 403 Forbidden
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "non-admin JWTs must be rejected with 403 on /api/admin/* (FR-022)");
    }

    [SkipUnlessDockerAvailable]
    public async Task AdminEndpoint_WithAdministratorJwt_Returns200()
    {
        await ApplyMigrationsAsync();
        HttpClient client = CreateClient();

        // Obtain an admin JWT via the Testing-only /e2e/dev-login endpoint
        HttpResponseMessage loginResponse = await client.PostAsJsonAsync(
            "/e2e/dev-login", new { Email = "admin@test.invalid", Role = "Administrator" });
        loginResponse.StatusCode.Should().Be(HttpStatusCode.OK, "dev-login must succeed in Testing env");

        DevLoginResponse? dto = await loginResponse.Content.ReadFromJsonAsync<DevLoginResponse>();
        dto.Should().NotBeNull();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", dto!.Token);

        // Act — admin JWT against admin endpoint
        HttpResponseMessage response = await client.GetAsync("/api/admin/users");

        // Assert — FR-022: administrator receives 200 OK
        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "Administrator-role JWTs must be accepted on /api/admin/* (FR-022)");
    }

    [SkipUnlessDockerAvailable]
    public async Task AdminEndpoint_WithMalformedRoleClaim_Returns403()
    {
        await ApplyMigrationsAsync();
        HttpClient client = CreateClient();

        string malformedToken = BuildTokenWithClaimType("x-role", "Administrator");
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", malformedToken);

        HttpResponseMessage response = await client.GetAsync("/api/admin/users");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "tokens with malformed role claim type must not satisfy AdminOnly policy");
    }

    private string BuildTokenWithClaimType(string claimType, string claimValue)
    {
        JwtConfiguration jwtConfig = ResolveJwtConfiguration();

        SymmetricSecurityKey key = new(Encoding.UTF8.GetBytes(jwtConfig.Key));
        SigningCredentials creds = new(key, SecurityAlgorithms.HmacSha256);

        List<Claim> claims =
        [
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new Claim(ClaimTypes.Email, "malformed-role@test.invalid"),
            new Claim(claimType, claimValue)
        ];

        JwtSecurityToken token = new(
            issuer: jwtConfig.Issuer,
            audience: jwtConfig.Audience,
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private JwtConfiguration ResolveJwtConfiguration()
    {
        IConfiguration configuration = GetServices()
            .GetRequiredService<IConfiguration>();

        return configuration.GetSection("Jwt").Get<JwtConfiguration>()
               ?? throw new InvalidOperationException("Jwt configuration section is missing.");
    }

    // Local DTO — mirrors the /e2e/dev-login JSON response shape
    private sealed record DevLoginResponse(string Token);
}
