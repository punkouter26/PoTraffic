using Microsoft.AspNetCore.Mvc;
using System.Text.Json.Serialization;

namespace PoTraffic.Api.Infrastructure;

internal static class BlazorClientHostExtensions
{
    /// <summary>
    /// Maps the endpoints that serve the hosted Blazor WASM client. All anonymous — the
    /// shell must load before sign-in, and client-side <c>&lt;AuthorizeRouteView&gt;</c>
    /// gates the UI. Static assets themselves are served by the
    /// <c>UseBlazorFrameworkFiles</c>/<c>UseStaticFiles</c> middleware, not here.
    /// </summary>
    internal static WebApplication MapBlazorClientHost(this WebApplication app)
    {
        // Unknown API routes 404 instead of falling through to the SPA shell (which would
        // serve index.html for an API call and, under the §4.5 deny-by-default policy, turn a
        // method mismatch into a misleading 401). Real endpoints are more specific, so win.
        app.Map("/api/{**rest}", () => Results.NotFound()).AllowAnonymous().ExcludeFromDescription();

        // Client-side unhandled error sink — paired with the blazor-error-ui observer in
        // wwwroot/js/blazor-error-reporter.js. Structured fields let App Insights filter by
        // route / UA / app version; the body is never echoed back to the caller.
        app.MapPost("/api/diag/client-error", (
            [FromBody] ClientErrorReport report,
            [FromServices] ILoggerFactory loggerFactory) =>
        {
            loggerFactory.CreateLogger("ClientErrors").LogWarning(
                "ClientError url={Url} ua={UserAgent} version={AppVersion} message={Message}",
                report.Url, report.UserAgent, report.AppVersion, report.Message);
            return Results.NoContent();
        }).AllowAnonymous().ExcludeFromDescription();

        // SPA fallback — any non-API GET returns index.html so the Blazor Router takes over.
        app.MapFallbackToFile("index.html").AllowAnonymous();

        return app;
    }
}

/// <summary>
/// Payload for the client-side unhandled error reports posted to <c>/api/diag/client-error</c>.
/// Fields stay primitive so the System.Text.Json source-generated payloads work under AOT.
/// </summary>
public sealed record ClientErrorReport(
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("userAgent")] string? UserAgent,
    [property: JsonPropertyName("appVersion")] string? AppVersion,
    [property: JsonPropertyName("message")] string? Message,
    [property: JsonPropertyName("stack")] string? Stack);
