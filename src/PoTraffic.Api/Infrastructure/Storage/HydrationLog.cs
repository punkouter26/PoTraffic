using Microsoft.Extensions.Logging;

namespace PoTraffic.Api.Infrastructure.Storage;

/// <summary>
/// Source-generated (zero-allocation) log events for the Table Storage hydration
/// path. EventId ranges:
///   9001–9010  Hydration lifecycle (start, table loaded, complete)
///   9101–9110  Storage authorization failures (401/403 — diagnostic for ops)
///   9201–9210  Generic hydration failures
///
/// All events surface in App Insights under the same <c>Operation</c> dimension
/// so a query like <c>customDimensions.Operation == "HydrateAsync"</c> captures
/// the entire failure tree — including RBAC propagation timing.
/// </summary>
internal static partial class HydrationLog
{
    // ── Lifecycle ────────────────────────────────────────────────────────────
    [LoggerMessage(EventId = 9001, Level = LogLevel.Information,
        Message = "Hydrating Table Storage ({Store}); ensuring {TableCount} tables exist.")]
    internal static partial void EnsuringTables(ILogger logger, string store, int tableCount);

    [LoggerMessage(EventId = 9002, Level = LogLevel.Information,
        Message = "Loaded {RowCount} rows from table '{Table}'.")]
    internal static partial void TableLoaded(ILogger logger, string table, int rowCount);

    [LoggerMessage(EventId = 9003, Level = LogLevel.Information,
        Message = "Hydration complete: {EntityCount} entities in working set.")]
    internal static partial void HydrationComplete(ILogger logger, int entityCount);

    [LoggerMessage(EventId = 9004, Level = LogLevel.Debug,
        Message = "Hydration skipped: no ITableStore registered (volatile/test mode).")]
    internal static partial void NoStore(ILogger logger);

    // ── AuthZ (401/403) ──────────────────────────────────────────────────────
    [LoggerMessage(EventId = 9101, Level = LogLevel.Error,
        Message = "Storage authorization FAILED for {Store}: HTTP {Status} {ErrorCode}. " +
                  "Grant 'Storage Table Data Contributor' on the storage account to the " +
                  "managed identity the app is running under (user-assigned via AZURE_CLIENT_ID " +
                  "or system-assigned on the App Service). RBAC propagation may take 3–5 minutes.")]
    internal static partial void StorageAuthzDenied(
        ILogger logger, int status, string errorCode, string store);

    // ── Generic failure ──────────────────────────────────────────────────────
    [LoggerMessage(EventId = 9201, Level = LogLevel.Error,
        Message = "Hydration failed: {ExceptionType}: {Message}")]
    internal static partial void HydrationFailed(ILogger logger, string exceptionType, string message);
}