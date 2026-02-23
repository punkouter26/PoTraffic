using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using PoTraffic.IntegrationTests.Helpers;
using PoTraffic.Shared.DTOs.Admin;
using PoTraffic.Shared.Enums;

namespace PoTraffic.IntegrationTests.Features.Admin;

/// <summary>
/// Integration tests for the Triple Test feature (FR-TT).
/// Verifies end-to-end HTTP behaviour: start a session, poll for results, auth boundaries.
/// </summary>
public sealed class TripleTestIntegrationTests : BaseIntegrationTest
{
    // Template Method — set Testing environment so /e2e/dev-login bypass is registered
    protected override void ConfigureHost(IWebHostBuilder builder) =>
        builder.UseEnvironment("Testing");

    // ── POST /api/admin/triple-test ──────────────────────────────────────────

    [SkipUnlessDockerAvailable]
    public async Task StartTripleTest_AsAdmin_Returns202WithSessionId()
    {
        await ApplyMigrationsAsync();
        HttpClient client = CreateClient();
        await AuthenticateAsAdminAsync(client);

        var request = new TripleTestRequest(
            "1 Apple Park Way, Cupertino, CA",
            "1 Infinite Loop, Cupertino, CA",
            RouteProvider.GoogleMaps,
            null);

        // Act
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/admin/triple-test", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Accepted,
            "valid triple test should return 202 Accepted");

        var body = await response.Content.ReadFromJsonAsync<StartResponse>();
        body.Should().NotBeNull();
        body!.SessionId.Should().NotBe(Guid.Empty, "a new session ID must be returned");
    }

    [SkipUnlessDockerAvailable]
    public async Task StartTripleTest_AsCommuter_Returns403()
    {
        await ApplyMigrationsAsync();
        HttpClient client = CreateClient();
        await AuthenticateAsCommuterAsync(client);

        var request = new TripleTestRequest("A", "B", RouteProvider.GoogleMaps, null);

        // Act
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/admin/triple-test", request);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "FR-TT: non-admin must be rejected with 403");
    }

    [SkipUnlessDockerAvailable]
    public async Task StartTripleTest_Unauthenticated_Returns401()
    {
        await ApplyMigrationsAsync();
        HttpClient client = CreateClient();
        // No auth header — unauthenticated

        var request = new TripleTestRequest("A", "B", RouteProvider.GoogleMaps, null);

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/admin/triple-test", request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── GET /api/admin/triple-test/{sessionId} ────────────────────────────────

    [SkipUnlessDockerAvailable]
    public async Task GetTripleTestSession_AfterStart_Returns200WithCorrectShape()
    {
        await ApplyMigrationsAsync();
        HttpClient client = CreateClient();
        await AuthenticateAsAdminAsync(client);

        // Start a session first
        var request = new TripleTestRequest(
            "1 Apple Park Way, Cupertino, CA",
            "1 Infinite Loop, Cupertino, CA",
            RouteProvider.GoogleMaps,
            null);

        HttpResponseMessage startResponse = await client.PostAsJsonAsync("/api/admin/triple-test", request);
        startResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var startBody = await startResponse.Content.ReadFromJsonAsync<StartResponse>();

        // Act — retrieve the session immediately (shots still pending)
        HttpResponseMessage getResponse = await client.GetAsync(
            $"/api/admin/triple-test/{startBody!.SessionId}");

        // Assert
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            "newly created session must be retrievable");

        TripleTestSessionDto? dto = await getResponse.Content
            .ReadFromJsonAsync<TripleTestSessionDto>();

        dto.Should().NotBeNull();
        dto!.SessionId.Should().Be(startBody.SessionId);
        dto.Shots.Should().HaveCount(3, "3 shot stubs must be created immediately");
        dto.Shots.Select(s => s.ShotIndex).Should().BeEquivalentTo([0, 1, 2]);
        dto.Shots.Select(s => s.OffsetSeconds).Should().BeEquivalentTo([0, 20, 40]);
        dto.Shots.All(s => s.IsSuccess == null).Should().BeTrue(
            "shots should be pending immediately after scheduling");
    }

    [SkipUnlessDockerAvailable]
    public async Task GetTripleTestSession_UnknownId_Returns404()
    {
        await ApplyMigrationsAsync();
        HttpClient client = CreateClient();
        await AuthenticateAsAdminAsync(client);

        HttpResponseMessage response = await client.GetAsync(
            $"/api/admin/triple-test/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    private async Task AuthenticateAsAdminAsync(HttpClient client)
    {
        HttpResponseMessage resp = await client.PostAsJsonAsync(
            "/e2e/dev-login", new { Email = "admin@test.invalid", Role = "Administrator" });
        var dto = await resp.Content.ReadFromJsonAsync<DevLoginResponse>();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", dto!.Token);
    }

    private async Task AuthenticateAsCommuterAsync(HttpClient client)
    {
        HttpResponseMessage resp = await client.PostAsJsonAsync(
            "/e2e/dev-login", new { Email = "commuter@test.invalid", Role = "Commuter" });
        var dto = await resp.Content.ReadFromJsonAsync<DevLoginResponse>();
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", dto!.Token);
    }

    private sealed record DevLoginResponse(string Token);
    private sealed record StartResponse(Guid SessionId);
}
