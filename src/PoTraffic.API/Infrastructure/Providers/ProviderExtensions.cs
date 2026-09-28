using PoTraffic.API.Features.Config;
using PoTraffic.API.Infrastructure.Resilience;
using PoTraffic.API.Infrastructure.Testing;

namespace PoTraffic.API.Infrastructure.Providers;

public static class ProviderExtensions
{
    /// <summary>
    /// Registers the Google Maps traffic provider plus the weather and holiday feeds, swapped
    /// for mocks in Testing or when Features:UseMockProviders is true. Each live
    /// <see cref="HttpClient"/> is wired into its named resilience pipeline.
    /// </summary>
    public static IServiceCollection AddTrafficProviders(this IServiceCollection services, IConfiguration configuration, IWebHostEnvironment environment)
    {
        // Resolved once and registered so health checks and /api/system/features act on the
        // same answer this registration did (see FeatureFlags).
        FeatureFlags flags = FeatureFlags.Resolve(configuration, environment);
        services.AddSingleton(flags);

        // Weather is registered unconditionally so ExecutePollCommand can take the
        // dependency without a null check — the flag decides whether it is consulted.
        if (flags.UseMockProviders)
        {
            // Integration/E2E isolation or local dev — replace production providers with mocks
            services.AddScoped<ITrafficProvider, MockTrafficProvider>();
            services.AddScoped<IWeatherProvider, MockWeatherProvider>();
            services.AddSingleton<IHolidayCalendar>(NoHolidayCalendar.Instance);
        }
        else
        {
            services.AddHttpClient<GoogleMapsTrafficProvider>()
                .AddResilienceHandler(ResiliencePipelineExtensions.TrafficPipeline);
            services.AddScoped<ITrafficProvider>(sp => sp.GetRequiredService<GoogleMapsTrafficProvider>());
            services.AddHttpClient<OpenMeteoWeatherProvider>()
                .AddResilienceHandler(ResiliencePipelineExtensions.WeatherPipeline);
            services.AddScoped<IWeatherProvider>(sp => sp.GetRequiredService<OpenMeteoWeatherProvider>());
            services.AddHttpClient<IHolidayCalendar, NagerHolidayCalendar>()
                .AddResilienceHandler(ResiliencePipelineExtensions.WeatherPipeline);
        }

        // Incident explanations for alerts. A no-op without TomTom:ApiKey, so it is safe to
        // register unconditionally (mock mode included — no key, no calls).
        services.AddHttpClient<IIncidentProvider, TomTomIncidentProvider>()
            .AddResilienceHandler(ResiliencePipelineExtensions.TrafficPipeline);

        return services;
    }
}
