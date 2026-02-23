namespace PoTraffic.Shared.DTOs.History;

/// <summary>
/// Full baseline response for a route on a given day of week.
/// Null when fewer than <c>BaselineMinSessionCount</c> qualifying sessions exist.
/// </summary>
public sealed record BaselineResponse(
    Guid RouteId,
    string DayOfWeek,
    int SessionCount,
    IReadOnlyList<BaselineSlotDto> Slots);
