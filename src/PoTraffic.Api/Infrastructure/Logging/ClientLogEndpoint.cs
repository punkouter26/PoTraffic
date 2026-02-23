using Microsoft.AspNetCore.Mvc;

namespace PoTraffic.Api.Infrastructure.Logging;

/// <summary>Represents a single log entry forwarded from the Blazor WASM client.</summary>
public sealed record ClientLogEntry(
    string Level,
    string Message,
    string? SourceContext,
    string? CorrelationId,
    string? SessionId,
    DateTimeOffset Timestamp);

/// <summary>Batch of log entries forwarded from the Blazor WASM client.</summary>
public sealed record ClientLogBatchRequest(List<ClientLogEntry> Entries);

public static class ClientLogEndpoints
{
    // Marker nested type used as ILogger<T> category — avoids polluting the public API surface
    private sealed class LogCategory;

    private const int MaxEntries = 50;

    public static IEndpointRouteBuilder MapClientLogEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/client-logs", Handle)
           .RequireAuthorization()
           .WithName("PostClientLogs")
           .WithTags("Logging");

        return app;
    }

    private static IResult Handle(
        [FromBody] ClientLogBatchRequest request,
        ILogger<LogCategory> logger)
    {
        if (request.Entries is null || request.Entries.Count == 0)
            return Results.NoContent();

        // Cap at MaxEntries to prevent log-flooding abuse
        IEnumerable<ClientLogEntry> entries = request.Entries.Take(MaxEntries);

        foreach (ClientLogEntry entry in entries)
        {
            LogLevel level = entry.Level?.ToUpperInvariant() switch
            {
                "CRITICAL" or "FATAL" => LogLevel.Critical,
                "ERROR"               => LogLevel.Error,
                "WARNING" or "WARN"   => LogLevel.Warning,
                "DEBUG"               => LogLevel.Debug,
                "TRACE"               => LogLevel.Trace,
                _                     => LogLevel.Information
            };

            logger.Log(
                level,
                "[WASM] [{SourceContext}] [{CorrelationId}] [{SessionId}] {Message}",
                entry.SourceContext,
                entry.CorrelationId,
                entry.SessionId,
                entry.Message);
        }

        return Results.NoContent();
    }
}
