using System.Net;
using System.Net.Http.Json;

namespace PoTraffic.IntegrationTests.Features.Config;

public sealed class SystemEndpointsIntegrationTests : BaseIntegrationTest
{
    [SkipUnlessAzuriteAvailable]
    public async Task GetFeatureFlags_InTesting_ReturnsMockProvidersEnabled()
    {
        HttpClient client = CreateClient();

        HttpResponseMessage response = await client.GetAsync("/api/system/features");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        FeatureFlagsResponse? flags = await response.Content.ReadFromJsonAsync<FeatureFlagsResponse>();
        flags.Should().NotBeNull();
        flags!.UseMockProviders.Should().BeTrue();
    }

    private sealed record FeatureFlagsResponse(bool TripleTestEnabled, bool UseMockProviders);
}
