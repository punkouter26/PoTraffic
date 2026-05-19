namespace PoTraffic.Api.Features.Config;

/// <summary>
/// Exposes read-only feature flags to the Blazor WASM client.
/// No authentication required — flags are not sensitive.
/// </summary>
public static class SystemEndpoints
{
    public static IEndpointRouteBuilder MapSystemEndpoints(this IEndpointRouteBuilder app)
    {
        // §10 — expose UseMockProviders so the client can display the "USING MOCK DATA" banner
        app.MapGet("/api/system/features", (IConfiguration config) =>
            Results.Ok(new
            {
                tripleTestEnabled = config.GetValue<bool>("Features:TripleTestEnabled", true),
                useMockProviders = config.GetValue<bool>("Features:UseMockProviders", false)
            }))
            .AllowAnonymous()
            .WithName("GetFeatureFlags")
            .WithTags("System");

        return app;
    }
}
