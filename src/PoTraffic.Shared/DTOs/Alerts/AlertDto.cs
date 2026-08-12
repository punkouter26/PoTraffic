namespace PoTraffic.Shared.DTOs.Alerts;

public sealed record AlertDto(
    AlertId Id,
    RouteId RouteId,
    string Kind,
    string Message,
    int TravelSeconds,
    int BaselineSeconds,
    DateTimeOffset CreatedAt,
    bool IsRead);
