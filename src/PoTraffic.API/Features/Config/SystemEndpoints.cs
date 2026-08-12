namespace PoTraffic.API.Features.Config;

/// <summary>
/// Exposes read-only feature flags to the Blazor WASM client.
/// No authentication required — flags are not sensitive.
/// </summary>
public static class SystemEndpoints
{
    public static IEndpointRouteBuilder MapSystemEndpoints(this IEndpointRouteBuilder app)
    {
        // §10 — expose UseMockProviders so the client can display the "USING MOCK DATA" banner.
        // Reads the same resolved FeatureFlags the provider registration acted on, so the
        // banner can't disagree with which providers are actually wired up.
        app.MapGet("/api/system/features", (FeatureFlags flags) =>
            Results.Ok(new
            {
                useMockProviders = flags.UseMockProviders
            }))
            .AllowAnonymous()
            .WithName("GetFeatureFlags")
            .WithTags("System");

        return app;
    }
}
