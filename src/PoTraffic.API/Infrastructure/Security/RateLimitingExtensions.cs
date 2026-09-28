using System.Security.Claims;
using System.Threading.RateLimiting;

namespace PoTraffic.API.Infrastructure.Security;

/// <summary>
/// Per-user limits on the endpoints that spend money on a paid provider (Google Maps
/// Directions/Geocoding/Places). Without them one stuck client or scripted loop turns into a
/// provider bill. Partitioned by user id (by IP before sign-in).
/// </summary>
public static class RateLimitingExtensions
{
    /// <summary>Check-now, route creation, return trip: one provider call (or two) each.</summary>
    public const string ProviderCalls = "provider-calls";

    /// <summary>Address autocomplete: one call per debounced keystroke, so a far higher ceiling.</summary>
    public const string Autocomplete = "autocomplete";

    public static IServiceCollection AddPoTrafficRateLimiting(this IServiceCollection services, IWebHostEnvironment env)
    {
        // E2E scenarios drive these endpoints far faster than a person; they test behaviour, not limits.
        int scale = env.IsEnvironment("Testing") ? 1000 : 1;

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy(ProviderCalls, ctx => PerUser(ctx, 20 * scale, TimeSpan.FromMinutes(10)));
            options.AddPolicy(Autocomplete, ctx => PerUser(ctx, 300 * scale, TimeSpan.FromMinutes(10)));
        });
        return services;
    }

    private static RateLimitPartition<string> PerUser(HttpContext ctx, int permits, TimeSpan window) =>
        RateLimitPartition.GetFixedWindowLimiter(
            ctx.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = permits, Window = window });
}
