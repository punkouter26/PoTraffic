using System.Net;
using FluentValidation;
using Microsoft.AspNetCore.Diagnostics;

namespace PoTraffic.API.Infrastructure;

/// <summary>
/// Global exception handler — maps known exception types to structured HTTP responses.
/// <list type="bullet">
///   <item><see cref="ValidationException"/> → 422 Unprocessable Entity</item>
///   <item><see cref="GeocodingConfigurationException"/> → 422 with <see cref="RouteErrorCodes.MapsKeyMissing"/></item>
/// </list>
/// Mapping happens here, by exception type, so every call site is covered by construction —
/// a per-handler <c>catch</c> only covers the one handler that remembered to write it.
/// </summary>
public sealed class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is ValidationException validationException)
        {
            logger.LogWarning(
                "Validation failed for {Path}: {Errors}",
                httpContext.Request.Path,
                string.Join("; ", validationException.Errors.Select(e => e.ErrorMessage)));

            httpContext.Response.StatusCode = (int)HttpStatusCode.UnprocessableEntity;
            await httpContext.Response.WriteAsJsonAsync(new
            {
                type = "https://tools.ietf.org/html/rfc4918#section-11.2",
                title = "Validation Failed",
                status = 422,
                errors = validationException.Errors
                    .GroupBy(e => e.PropertyName)
                    .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray())
            }, cancellationToken);

            return true;
        }

        // Server misconfiguration (e.g. a missing Maps key), not bad user input. Handled here
        // rather than per-handler: create-route caught it and answered with an actionable code,
        // while the update / verify-address / triple-test paths let the identical failure
        // surface as an opaque 500.
        if (exception is GeocodingConfigurationException geocodeConfigEx)
        {
            logger.LogError(geocodeConfigEx, "Geocoding is not configured on the server ({Path}).",
                httpContext.Request.Path);

            httpContext.Response.StatusCode = (int)HttpStatusCode.UnprocessableEntity;
            await httpContext.Response.WriteAsJsonAsync(new
            {
                type = "https://tools.ietf.org/html/rfc4918#section-11.2",
                title = "Geocoding Not Configured",
                status = 422,
                error = RouteErrorCodes.MapsKeyMissing
            }, cancellationToken);

            return true;
        }

        return false;
    }
}
