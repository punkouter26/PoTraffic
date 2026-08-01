namespace PoTraffic.Shared.DTOs.History;

/// <summary>Mean travel duration for one day-of-week across a route's whole history.</summary>
public sealed record WeekdayMeanDto(
    string DayOfWeek,
    double MeanDurationSeconds,
    int SampleCount);

/// <summary>
/// Per-weekday travel-time comparison for a route, powering "Fridays run 20% worse"
/// style insights. <see cref="BestDay"/>/<see cref="WorstDay"/> are the fastest/slowest
/// days that have at least one sample; both null until any polls exist.
/// </summary>
public sealed record WeekdayComparisonDto(
    RouteId RouteId,
    IReadOnlyList<WeekdayMeanDto> Days,
    string? BestDay,
    string? WorstDay);
