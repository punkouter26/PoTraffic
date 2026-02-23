namespace PoTraffic.Api.Infrastructure.Data.Projections;

/// <summary>
/// Keyless projection used by global-volatility raw-SQL aggregation.
/// Provider is stored as int to match the database column type.
/// </summary>
public sealed class GlobalVolatilityProjection
{
    public string DayOfWeek { get; set; } = string.Empty;
    public int TimeSlotBucket { get; set; }
    public int ProviderInt { get; set; }
    public double MeanDurationSeconds { get; set; }
    public double? StdDevDurationSeconds { get; set; }
    public int RouteCount { get; set; }
}
