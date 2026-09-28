using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PoTraffic.API.Infrastructure.Providers;

namespace PoTraffic.IntegrationTests.Features.Config;

public sealed class CostGuardrailsIntegrationTests : BaseIntegrationTest
{
    [SkipUnlessAzuriteAvailable]
    public void TestingConfiguration_NeverResolvesTheBilledProvider()
    {
        IServiceProvider services = GetServices();

        services.GetRequiredService<IConfiguration>().GetValue<bool>("Features:UseMockProviders").Should().BeTrue();

        using IServiceScope scope = services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITrafficProvider>()
            .Should().NotBeOfType<GoogleMapsTrafficProvider>();
    }
}
