namespace PoTraffic.Shared.DTOs.Admin;

/// <summary>
/// A single 5-minute aggregated data point for the recent (past 24h) global volatility chart.
/// Unlike GlobalVolatilitySlotDto this carries a real UTC timestamp so the X-axis shows
/// actual wall-clock time rather than a day-of-week + bucket offset.
/// </summary>
public sealed record RecentVolatilityPointDto(
    DateTime PolledAt,
    double MeanDurationSeconds,
    int RouteCount);
