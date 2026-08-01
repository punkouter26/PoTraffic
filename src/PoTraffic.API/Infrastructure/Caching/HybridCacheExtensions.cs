// filepath: src/PoTraffic.API/Infrastructure/Caching/HybridCacheExtensions.cs
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Caching.Hybrid;

namespace PoTraffic.API.Infrastructure.Caching;

/// <summary>
/// Registers <see cref="HybridCache"/> (in-process L1 + distributed L2) for
/// read-heavy slices: <c>Features/Config</c> (system configuration),
/// <c>Features/Routes</c> (route catalogue), and provider discovery.
///
/// <para>
/// Falls back to the in-process L1 only when no distributed cache is configured
/// (Development / Testing). Production backs this with Azure Redis via the
/// standard <c>AddStackExchangeRedisCache(...).AddHybridCache(...)</c> chain.
/// </para>
/// </summary>
public static class HybridCacheExtensions
{
    public static IServiceCollection AddPoTrafficHybridCache(this IServiceCollection services)
    {
        services.AddHybridCache(options =>
        {
            // Routes are polled often; keep cache entry TTL short so a Check Now
            // is reflected on the next page refresh.
            options.DefaultEntryOptions = new HybridCacheEntryOptions
            {
                Expiration = TimeSpan.FromMinutes(2),
                LocalCacheExpiration = TimeSpan.FromSeconds(60),
            };
        });
        return services;
    }
}
