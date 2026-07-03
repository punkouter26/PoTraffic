using System.Security.Claims;
using Serilog.Context;

namespace PoTraffic.Api.Infrastructure.Logging;

/// <summary>
/// Middleware — enriches the Serilog LogContext for every request.
/// Ensures all log events carry these properties consistently across all sinks (file, console, App Insights).
/// Must be registered after UseAuthentication so that User claims are available.
/// </summary>
public sealed class LogContextEnrichmentMiddleware(RequestDelegate next, IWebHostEnvironment env)
{
    public async Task InvokeAsync(HttpContext ctx)
    {
        // Cookie/BFF sessions carry identity on ClaimTypes.NameIdentifier (what the rest of
        // the app resolves). Reading only "sub" tagged every such request UserId=anonymous.
        string userId = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier)
                     ?? ctx.User.FindFirstValue("sub")
                     ?? "anonymous";
        string correlationId = ctx.Request.Headers.TryGetValue("X-Correlation-Id", out var correlationHeader)
            ? correlationHeader.ToString()
            : ctx.TraceIdentifier;
        string sessionId = ctx.Request.Headers.TryGetValue("X-Session-Id", out var sessionHeader)
            ? sessionHeader.ToString()
            : "none";

        using (LogContext.PushProperty("UserId", userId))
        using (LogContext.PushProperty("SessionId", sessionId))
        using (LogContext.PushProperty("CorrelationId", correlationId))
        using (LogContext.PushProperty("Environment", env.EnvironmentName))
        {
            await next(ctx);
        }
    }
}
