using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using PoTraffic.IntegrationTests.Helpers;

namespace PoTraffic.IntegrationTests.Features.Admin;

/// <summary>
/// Integration tests for the AdminOnly RBAC policy (FR-022).
/// Verifies that Commuter-role cookie sessions receive 403 Forbidden on all
/// /api/admin/* endpoints, Administrator sessions receive 200 OK, and
/// anonymous requests receive 401.
/// </summary>
public sealed class AdminAuthorizationIntegrationTests : BaseIntegrationTest
{
    // Template Method — set Testing environment so /e2e/dev-login bypass is registered.
    protected override void ConfigureHost(IWebHostBuilder builder) =>
        builder.UseEnvironment("Testing");

    [SkipUnlessAzuriteAvailable]
    public async Task AdminEndpoint_WithCommuterSession_Returns403()
    {
        await ApplyMigrationsAsync();
        HttpClient client = CreateClient();

        // Establish a commuter cookie session via the Testing-only /e2e/dev-login endpoint (FR-022)
        HttpResponseMessage loginResponse = await client.PostAsJsonAsync(
            "/e2e/dev-login", new { Email = "commuter@test.invalid", Role = "Commuter" });
        loginResponse.StatusCode.Should().Be(HttpStatusCode.OK, "dev-login must succeed in Testing env");

        // Act — commuter session against admin endpoint
        HttpResponseMessage response = await client.GetAsync("/api/admin/users");

        // Assert — FR-022: non-admin receives 403 Forbidden
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "non-admin sessions must be rejected with 403 on /api/admin/* (FR-022)");
    }

    [SkipUnlessAzuriteAvailable]
    public async Task AdminEndpoint_WithAdministratorSession_Returns200()
    {
        await ApplyMigrationsAsync();
        HttpClient client = CreateClient();

        // Establish an admin cookie session via the Testing-only /e2e/dev-login endpoint
        HttpResponseMessage loginResponse = await client.PostAsJsonAsync(
            "/e2e/dev-login", new { Email = "admin@test.invalid", Role = "Administrator" });
        loginResponse.StatusCode.Should().Be(HttpStatusCode.OK, "dev-login must succeed in Testing env");

        // Act — admin session against admin endpoint
        HttpResponseMessage response = await client.GetAsync("/api/admin/users");

        // Assert — FR-022: administrator receives 200 OK
        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "Administrator-role sessions must be accepted on /api/admin/* (FR-022)");
    }

    [SkipUnlessAzuriteAvailable]
    public async Task AdminEndpoint_WithoutSession_Returns401()
    {
        await ApplyMigrationsAsync();
        HttpClient client = CreateClient();

        HttpResponseMessage response = await client.GetAsync("/api/admin/users");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "anonymous requests must not satisfy the AdminOnly policy");
    }
}
