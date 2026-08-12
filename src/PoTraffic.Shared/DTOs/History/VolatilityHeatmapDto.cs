namespace PoTraffic.Shared.DTOs.History;

/// <summary>
/// Day-of-week × half-hour congestion grid for a route (#5).
///
/// <para>
/// Every cell is expressed relative to <see cref="MedianDurationSeconds"/> — the route's
/// own middle sample across its whole history — rather than an absolute duration, so a
/// 20-minute hop and a 90-minute haul colour on the same scale. Only day/half-hour pairs
/// that actually have samples are sent; the client draws the empty ones from the gaps.
/// </para>
///
/// <para>Day-of-week and time are US Eastern (the user's wall-clock day), so a 17:30 row
/// reads as the rush hour it actually is for the commuter driving it.</para>
/// </summary>
public sealed record VolatilityHeatmapDto(
    RouteId RouteId,
    double MedianDurationSeconds,
    int TotalSamples,
    IReadOnlyList<HeatmapCellDto> Cells);

/// <summary>
/// One half-hour of one weekday, expressed in US Eastern time.
/// <see cref="StdDevDurationSeconds"/> is 0 when the cell holds a single sample —
/// no spread can be computed from one point.
/// <para><see cref="Hour"/> is the local Eastern hour 0–23, and <see cref="HalfHour"/>
/// is 0 or 1 for the first or second half of that hour.</para>
/// </summary>
public sealed record HeatmapCellDto(
    string DayOfWeek,
    int Hour,
    int HalfHour,
    double MeanDurationSeconds,
    double StdDevDurationSeconds,
    int SampleCount);
