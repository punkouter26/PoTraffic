namespace PoTraffic.Shared.DTOs.History;

/// <summary>
/// Full baseline response for a route on a given day of week.
/// <see cref="DayOfWeekSpecific"/> is false when the requested weekday had too few
/// samples and the slots were computed from all days as a fallback (so a new route
/// still shows a usable baseline instead of a blank chart).
/// </summary>
public sealed record BaselineResponse(
    RouteId RouteId,
    string DayOfWeek,
    int SessionCount,
    IReadOnlyList<BaselineSlotDto> Slots,
    bool DayOfWeekSpecific = true);
