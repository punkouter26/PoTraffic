using PoTraffic.Shared.Enums;

namespace PoTraffic.Shared.DTOs.Admin;

/// <summary>
/// Global traffic volatility for a 5-minute slot across all active routes,
/// used by the Admin Dashboard volatility heatmap.
/// </summary>
public sealed record GlobalVolatilitySlotDto(
    string DayOfWeek,
    int TimeSlotBucket,
    double MeanDurationSeconds,
    double? StdDevDurationSeconds,
    int RouteCount,
    RouteProvider Provider);
