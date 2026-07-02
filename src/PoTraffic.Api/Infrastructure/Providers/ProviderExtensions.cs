using PoTraffic.Api.Infrastructure.Resilience;
using PoTraffic.Api.Infrastructure.Testing;
using PoTraffic.Shared.Enums;

namespace PoTraffic.Api.Infrastructure.Providers;

public static class ProviderExtensions
{
    /// <summary>
    /// Registers traffic providers as keyed services using the Strategy pattern.
    /// Swaps real providers for MockTrafficProvider in Testing or when Features:UseMockProviders is true.
    /// Each live <see cref="HttpClient"/> is wired into the named
    /// <see cref="ResiliencePipelineExtensions.TrafficPipeline"/>.
    /// </summary>
    public static IServiceCollection AddTrafficProviders(this IServiceCollection services, IConfiguration configuration, IWebHostEnvironment environment)
    {
        bool useMockProviders = environment.IsEnvironment("Testing")
            || configuration.GetValue<bool>("Features:UseMockProviders");

        if (useMockProviders)
        {
            // Integration/E2E isolation or local dev — replace production providers with mocks
            services.AddKeyedScoped<ITrafficProvider, MockTrafficProvider>(RouteProvider.GoogleMaps);
            services.AddKeyedScoped<ITrafficProvider, MockTrafficProvider>(RouteProvider.TomTom);
        }
        else
        {
            services.AddHttpClient<GoogleMapsTrafficProvider>()
                .AddResilienceHandler(ResiliencePipelineExtensions.TrafficPipeline);
            services.AddHttpClient<TomTomTrafficProvider>()
                .AddResilienceHandler(ResiliencePipelineExtensions.TrafficPipeline);
            services.AddKeyedScoped<ITrafficProvider, GoogleMapsTrafficProvider>(RouteProvider.GoogleMaps);
            services.AddKeyedScoped<ITrafficProvider, TomTomTrafficProvider>(RouteProvider.TomTom);
        }

        // Factory pattern — ITrafficProviderFactory hides IKeyedServiceProvider cast from handlers
        services.AddScoped<ITrafficProviderFactory, KeyedServiceTrafficProviderFactory>();

        return services;
    }
}
