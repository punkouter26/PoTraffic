using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PoTraffic.API.Infrastructure.Providers;
using PoTraffic.Shared.Enums;

namespace PoTraffic.Tests.Features.Config;

public sealed class CostGuardrailsIntegrationTests : BaseIntegrationTest
{
    [SkipUnlessAzuriteAvailable]
    public void TestingConfiguration_DisablesRemoteCostSurfaces()
    {
        IServiceProvider services = GetServices();
        IConfiguration configuration = services.GetRequiredService<IConfiguration>();

        configuration.GetValue<bool>("Features:UseMockProviders").Should().BeTrue();
        configuration.GetValue<bool>("Features:EnableAiFeatures").Should().BeFalse();
        configuration.GetValue<bool>("Features:EnableExternalTrafficProviders").Should().BeFalse();

        ITrafficProvider google = services.GetRequiredKeyedService<ITrafficProvider>(RouteProvider.GoogleMaps);
        ITrafficProvider tomTom = services.GetRequiredKeyedService<ITrafficProvider>(RouteProvider.TomTom);

        google.GetType().Name.Should().NotBe(nameof(GoogleMapsTrafficProvider));
        tomTom.GetType().Name.Should().NotBe(nameof(TomTomTrafficProvider));
    }
}
