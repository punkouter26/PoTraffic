namespace PoTraffic.Shared.DTOs.History;

/// <summary>
/// Day-of-week × hour congestion grid for a route (#5).
///
/// <para>
/// Every cell is expressed relative to <see cref="MedianDurationSeconds"/> — the route's
/// own middle sample across its whole history — rather than an absolute duration, so a
/// 20-minute hop and a 90-minute haul colour on the same scale. Only hour/day pairs that
/// actually have samples are sent; the client draws the empty ones from the gaps.
/// </para>
///
/// <para>Day-of-week and hour are UTC, matching the polling and baseline model.</para>
/// </summary>
public sealed record VolatilityHeatmapDto(
    RouteId RouteId,
    double MedianDurationSeconds,
    int TotalSamples,
    IReadOnlyList<HeatmapCellDto> Cells);

/// <summary>
/// One hour of one weekday. <see cref="StdDevDurationSeconds"/> is 0 when the cell holds a
/// single sample — no spread can be computed from one point.
/// </summary>
public sealed record HeatmapCellDto(
    string DayOfWeek,
    int Hour,
    double MeanDurationSeconds,
    double StdDevDurationSeconds,
    int SampleCount);
