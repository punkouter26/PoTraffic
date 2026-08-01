using Azure;
using Azure.Data.Tables;

namespace PoTraffic.API.Infrastructure.Scheduling;

/// <summary>
/// Azure Table Storage entity representing a scheduled background job.
/// Stores durable job state in Azurite locally and Azure Table Storage in production.
/// </summary>
public sealed class ScheduledJobEntity : ITableEntity
{
    public string PartitionKey { get; set; } = string.Empty;
    public string RowKey { get; set; } = string.Empty;
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    /// <summary>Fully qualified type name of the job class (e.g. "PoTraffic.API.Features.Routes.PollRouteJob").</summary>
    public string TypeName { get; set; } = string.Empty;

    /// <summary>Method name to invoke (e.g. "Execute").</summary>
    public string MethodName { get; set; } = string.Empty;

    /// <summary>JSON-serialized method arguments (e.g. "[\"route-id-guid\"]").</summary>
    public string ArgsJson { get; set; } = "[]";

    /// <summary>When the job should fire. UTC ticks stored as long.</summary>
    public DateTimeOffset FireAt { get; set; }

    /// <summary>Job status: Pending, Running, Completed, Cancelled.</summary>
    public string Status { get; set; } = "Pending";

    /// <summary>
    /// UTC time of day ("HH:mm") at which a recurring job fires; null for one-shot jobs.
    /// Stored as a string because Table Storage has no TimeOnly column type.
    /// </summary>
    public string? DailyAtUtc { get; set; }

    /// <summary>Last time this recurring job ran (null if never).</summary>
    public DateTimeOffset? LastRunAt { get; set; }

    /// <summary>Optional error message from the last failed execution.</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>Number of execution attempts.</summary>
    public int AttemptCount { get; set; }
}
