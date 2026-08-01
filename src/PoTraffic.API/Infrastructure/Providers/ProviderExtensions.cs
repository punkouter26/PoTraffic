using PoTraffic.API.Features.Config;
using PoTraffic.API.Infrastructure.Resilience;
using PoTraffic.API.Infrastructure.Testing;
using PoTraffic.Shared.Enums;

namespace PoTraffic.API.Infrastructure.Providers;

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
        // Resolved once and registered so health checks and /api/system/features act on the
        // same answer this registration did (see FeatureFlags).
        FeatureFlags flags = FeatureFlags.Resolve(configuration, environment);
        services.AddSingleton(flags);

        if (flags.UseMockProviders)
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
