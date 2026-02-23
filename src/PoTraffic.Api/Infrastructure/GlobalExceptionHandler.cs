using System.Net;
using FluentValidation;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace PoTraffic.Api.Infrastructure;

/// <summary>
/// Global exception handler — maps known exception types to structured HTTP responses.
/// <list type="bullet">
///   <item><see cref="ValidationException"/> → 422 Unprocessable Entity</item>
///   <item><see cref="DbUpdateException"/> (Unique Constraint) → 409 Conflict</item>
/// </list>
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

        if (exception is DbUpdateException dbEx && dbEx.InnerException is SqlException sqlEx && sqlEx.Number == 2601)
        {
            // Error 2601: Cannot insert duplicate key row in object with unique index.
            logger.LogWarning("Conflict detected at {Path}: {Message}", httpContext.Request.Path, sqlEx.Message);

            httpContext.Response.StatusCode = (int)HttpStatusCode.Conflict;
            await httpContext.Response.WriteAsJsonAsync(new
            {
                type = "https://tools.ietf.org/html/rfc7231#section-6.5.8",
                title = "Conflict",
                status = 409,
                detail = "This route already exists and is active."
            }, cancellationToken);

            return true;
        }

        return false;
    }
}
