using PoTraffic.Shared.DTOs.Admin;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using PoTraffic.API.Features.Config;
using PoTraffic.API.Infrastructure.Storage;

namespace PoTraffic.API.Infrastructure;

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
        bool usingMockProviders = FeatureFlags.Resolve(config, env).UseMockProviders;

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
    /// Maps the anonymous diagnostics endpoints:
    /// <list type="bullet">
    /// <item><c>/health/ready</c> — 503 while <see cref="TableStorageContext.HydrateAsync"/> is in
    /// flight, 200 once hydrated. Lets App Service gate traffic without depending on hydration
    /// during host bind.</item>
    /// <item><c>/health/json</c> — per-dependency JSON status for DB and external APIs. This is
    /// the machine-readable feed used by post-deploy smoke and triage scripts.</item>
    /// <item><c>/health/connections</c> — the richer per-connection list (health checks plus
    /// scheduler tick and provider-key configuration) rendered by the <c>/health</c> page.</item>
    /// </list>
    /// <para>
    /// Note that <c>/health</c> itself is deliberately NOT mapped here: it falls through to the
    /// SPA so the Blazor health page owns that route (Rule 3 — "/health (blazor page that shows
    /// all connection status)"). Machine consumers use <c>/health/json</c>.
    /// </para>
    /// </summary>
    internal static WebApplication MapPoTrafficHealthChecks(this WebApplication app)
    {
        app.MapGet("/health/ready", (TableStorageContext ctx) =>
            ctx.IsHydrated
                ? Results.Ok(new { status = "ready", durable = ctx.IsDurable })
                : Results.Json(new { status = "hydrating", durable = ctx.IsDurable }, statusCode: 503))
            .AllowAnonymous();

        app.MapGet("/health/connections", async (
            HealthCheckService healthCheckService,
            IConfiguration configuration,
            CancellationToken ct) =>
            Results.Ok(await ConnectionHealthReporter.BuildAsync(healthCheckService, configuration, ct)))
            .WithName("GetHealthConnections")
            .Produces<IEnumerable<ConnectionHealthDto>>()
            .AllowAnonymous();

        app.MapHealthChecks("/health/json", new HealthCheckOptions
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
