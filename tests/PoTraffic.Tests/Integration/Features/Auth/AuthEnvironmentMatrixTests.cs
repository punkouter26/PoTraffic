using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using PoTraffic.Tests.Helpers;

namespace PoTraffic.Tests.Features.Auth;

/// <summary>
/// Rule 4.4 environment matrix:
///   Testing     → guest bypass (for automated tests)
///   Development → Microsoft OAuth AND guest bypass
///   Production  → Microsoft OAuth ONLY (guest endpoint absent)
/// </summary>
public sealed class TestingAuthMatrixTests : BaseIntegrationTest
{
    [SkipUnlessAzuriteAvailable]
    public async Task Testing_GuestBypass_IsEnabled_AndWorks()
    {
        await ApplyMigrationsAsync();
        HttpClient client = CreateClient();

        JsonElement providers = await client.GetFromJsonAsync<JsonElement>("/api/auth/providers");
        providers.GetProperty("guestEnabled").GetBoolean().Should().BeTrue("Testing forces the guest bypass");

        (await client.PostAsync("/api/auth/guest-login", content: null)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/api/routes")).StatusCode.Should().Be(HttpStatusCode.OK,
            "a guest session must satisfy the auth policy in Testing");
    }
}

public sealed class DevelopmentAuthMatrixTests : BaseIntegrationTest
{
    protected override void ConfigureHost(IWebHostBuilder builder) =>
        builder.UseEnvironment("Development");

    [SkipUnlessAzuriteAvailable]
    public async Task Development_Offers_MicrosoftAndGuest_AndGuestSessionIsAccepted()
    {
        await ApplyMigrationsAsync();
        HttpClient client = CreateClient();

        JsonElement providers = await client.GetFromJsonAsync<JsonElement>("/api/auth/providers");
        providers.GetProperty("guestEnabled").GetBoolean().Should().BeTrue("Development shows the guest bypass");
        providers.GetProperty("providers").EnumerateArray().Select(p => p.GetString())
            .Should().Contain("microsoft", "Development also offers Microsoft OAuth");

        (await client.PostAsync("/api/auth/guest-login", content: null)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/api/routes")).StatusCode.Should().Be(HttpStatusCode.OK,
            "the ProductionMicrosoftAuth policy must accept guest sessions in Development (Rule 4.4)");
    }
}

public sealed class ProductionAuthMatrixTests : BaseIntegrationTest
{
    protected override void ConfigureHost(IWebHostBuilder builder) =>
        builder.UseEnvironment("Production");

    [SkipUnlessAzuriteAvailable]
    public async Task Production_Is_MicrosoftOnly_GuestEndpointAbsent()
    {
        await ApplyMigrationsAsync();
        HttpClient client = CreateClient();

        JsonElement providers = await client.GetFromJsonAsync<JsonElement>("/api/auth/providers");
        providers.GetProperty("guestEnabled").GetBoolean().Should().BeFalse("Production is Microsoft-only");

        // 404 or 405: the endpoint is not registered — POST falls through to the
        // GET-only SPA fallback route.
        (await client.PostAsync("/api/auth/guest-login", content: null)).StatusCode
            .Should().BeOneOf([HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed],
                "the guest endpoint must not be registered in Production");
    }
}
