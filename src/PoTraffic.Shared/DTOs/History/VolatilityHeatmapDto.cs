namespace PoTraffic.Shared.DTOs.History;

/// <summary>
/// Day-of-week × quarter-hour congestion grid for a route (#5).
///
/// <para>
/// Every cell is expressed relative to <see cref="MedianDurationSeconds"/> — the route's
/// own middle sample across its whole history — rather than an absolute duration, so a
/// 20-minute hop and a 90-minute haul colour on the same scale. Only day/half-hour pairs
/// that actually have samples are sent; the client draws the empty ones from the gaps.
/// </para>
///
/// <para>Day-of-week and time are bucketed in the user's local time zone
/// (<see cref="TimeZoneId"/>), so a 17:30 column reads as the rush hour it actually is
/// for the commuter driving it. The server stores every sample in UTC; this grouping
/// projects UTC onto the user's wall clock before bucketing.</para>
///
/// <para>Resolution is 15 minutes. The poller's base cadence is five minutes, so a
/// quarter-hour bucket still averages several samples while being fine enough to show
/// congestion building and clearing inside a single hour — which a half-hour bucket
/// flattened into one number.</para>
/// </summary>
public sealed record VolatilityHeatmapDto(
    RouteId RouteId,
    double MedianDurationSeconds,
    int TotalSamples,
    string TimeZoneId,
    IReadOnlyList<HeatmapCellDto> Cells);

/// <summary>
/// One quarter-hour of one weekday, in the user's local time zone.
/// <see cref="StdDevDurationSeconds"/> is 0 when the cell holds a single sample —
/// no spread can be computed from one point.
/// </summary>
/// <param name="Hour">Local hour, 0–23.</param>
/// <param name="Quarter">
/// Which quarter of that hour: 0 = :00, 1 = :15, 2 = :30, 3 = :45.
/// </param>
public sealed record HeatmapCellDto(
    string DayOfWeek,
    int Hour,
    int Quarter,
    double MeanDurationSeconds,
    double StdDevDurationSeconds,
    int SampleCount);
