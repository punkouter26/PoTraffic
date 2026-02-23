using System.Net;
using System.Net.Http.Json;
using PoTraffic.IntegrationTests.Helpers;
using Xunit;

namespace PoTraffic.IntegrationTests.Security;

/// <summary>
/// Verifies that the testing-only endpoints are NOT registered outside the Testing environment.
/// The API has <c>MapFallbackToFile("index.html")</c> for Blazor SPA routing, so raw GET
/// requests to unregistered paths return 200 with HTML. These tests therefore POST with
/// JSON bodies — the expected result in non-Testing environments is that the route match
/// fails entirely, yielding 404. If the app returns 405 (MethodNotAllowed) that also proves
/// the /e2e route group is not registered, since there is no matching endpoint.
/// </summary>
public sealed class TestingEndpointSecurityTests : BaseIntegrationTest
{
    public TestingEndpointSecurityTests() : base() { }

    /// <summary>
    /// /e2e/dev-login is registered ONLY when ASPNETCORE_ENVIRONMENT=Testing.
    /// In all other environments the route does not exist.
    /// POST with a JSON body verifies the endpoint is truly absent (not masked by SPA fallback).
    /// </summary>
    [SkipUnlessDockerAvailable]
    public async Task DevLoginEndpoint_ReturnsNon200_WhenNotInTestingEnvironment()
    {
        // Arrange — BaseIntegrationTest does not set ASPNETCORE_ENVIRONMENT=Testing
        HttpClient client = CreateClient();

        // Act — POST with valid JSON body (if endpoint existed, this would return 200 + JWT)
        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/e2e/dev-login", new { Email = "test@test.invalid", Role = "Commuter" });

        // Assert — endpoint not registered, so no 200 OK with a token is possible
        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// /e2e/seed is registered ONLY when ASPNETCORE_ENVIRONMENT=Testing.
    /// POST without a body: if the endpoint isn't registered, the result is 404 or 405.
    /// </summary>
    [SkipUnlessDockerAvailable]
    public async Task SeedEndpoint_ReturnsNon2xx_WhenNotInTestingEnvironment()
    {
        // Arrange
        HttpClient client = CreateClient();

        // Act
        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/e2e/seed", new { Scenario = "test" });

        // Assert — no 2xx response should be possible when endpoint is unregistered
        Assert.False(
            ((int)response.StatusCode) is >= 200 and < 300,
            $"Expected non-2xx for /e2e/seed in non-Testing env, but got {response.StatusCode}");
    }
}
