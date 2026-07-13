using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using PoTraffic.Api.Infrastructure.Storage;

namespace PoTraffic.Api.Infrastructure;

internal static class HealthCheckExtensions
{
    /// <summary>
    /// Registers the app's health checks — all cheap and free of external-quota cost.
    /// "keyvault" proves the Microsoft OAuth client secret resolved from the vault;
    /// "trafficProvider" confirms the live GoogleMaps key is present. Both short-circuit
    /// to Healthy when mock providers are in use (Testing, or Features:UseMockProviders).
    /// </summary>
    internal static IServiceCollection AddPoTrafficHealthChecks(
        this IServiceCollection services, IHostEnvironment env, IConfiguration config)
    {
        bool usingMockProviders = env.IsEnvironment("Testing")
            || string.Equals(config["Features:UseMockProviders"], "true", StringComparison.OrdinalIgnoreCase);

        services.AddHealthChecks()
            .AddCheck("keyvault", () =>
            {
                if (usingMockProviders)
                    return HealthCheckResult.Healthy("Key Vault not required (test/dev mock mode)");
                string? secret = config["ExternalAuth:Microsoft:ClientSecret"];
                bool resolved = !string.IsNullOrWhiteSpace(secret)
                    && !secret.StartsWith("REPLACE_WITH", StringComparison.OrdinalIgnoreCase);
                return resolved
                    ? HealthCheckResult.Healthy("Key Vault secrets resolved")
                    : HealthCheckResult.Unhealthy("Key Vault secret 'ExternalAuth:Microsoft:ClientSecret' not resolved");
            })
            .AddCheck("trafficProvider", () =>
            {
                if (usingMockProviders)
                    return HealthCheckResult.Healthy("Mock provider (test/dev)");
                return !string.IsNullOrWhiteSpace(config["GoogleMaps:ApiKey"])
                    ? HealthCheckResult.Healthy("Live GoogleMaps provider configured")
                    : HealthCheckResult.Degraded("GoogleMaps:ApiKey missing — live provider will fail");
            })
            .AddCheck<StorageHealthCheck>("storage")
            .AddCheck<SchedulerHealthCheck>("scheduler");

        return services;
    }

    /// <summary>
    /// Maps the two liveness/readiness endpoints, both anonymous:
    /// <list type="bullet">
    /// <item><c>/health/ready</c> — 503 while <see cref="TableStorageContext.HydrateAsync"/> is in
    /// flight, 200 once hydrated. Lets App Service gate traffic without depending on hydration
    /// during host bind.</item>
    /// <item><c>/health</c> — per-dependency JSON status for DB and external APIs.</item>
    /// </list>
    /// </summary>
    internal static WebApplication MapPoTrafficHealthChecks(this WebApplication app)
    {
        app.MapGet("/health/ready", (TableStorageContext ctx) =>
            ctx.IsHydrated
                ? Results.Ok(new { status = "ready", durable = ctx.IsDurable })
                : Results.Json(new { status = "hydrating", durable = ctx.IsDurable }, statusCode: 503))
            .AllowAnonymous();

        app.MapHealthChecks("/health", new HealthCheckOptions
        {
            ResponseWriter = async (httpCtx, report) =>
            {
                httpCtx.Response.ContentType = "application/json";
                await httpCtx.Response.WriteAsync(System.Text.Json.JsonSerializer.Serialize(new
                {
                    status = report.Status.ToString(),
                    entries = report.Entries.Select(e => new
                    {
                        name = e.Key,
                        status = e.Value.Status.ToString(),
                        description = e.Value.Description,
                        durationMs = e.Value.Duration.TotalMilliseconds
                    })
                }));
            }
        }).AllowAnonymous();

        return app;
    }
}
