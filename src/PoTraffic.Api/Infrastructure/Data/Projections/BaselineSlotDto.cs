namespace PoTraffic.Api.Infrastructure.Data.Projections;

/// <summary>
/// Keyless projection used by raw-SQL baseline queries.
/// Represents mean/stddev travel duration for a 5-minute time slot on a given day-of-week.
/// </summary>
public sealed class BaselineSlotDto
{
    public string DayOfWeek { get; set; } = string.Empty;

    /// <summary>Minutes from midnight, bucketed to 5-minute intervals.</summary>
    public int TimeSlotBucket { get; set; }

    public double MeanDurationSeconds { get; set; }
    public double? StdDevDurationSeconds { get; set; }
    public int SessionCount { get; set; }
}
