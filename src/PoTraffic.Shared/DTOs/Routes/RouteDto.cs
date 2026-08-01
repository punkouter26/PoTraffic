using PoTraffic.Shared.Enums;

namespace PoTraffic.Shared.DTOs.Routes;

public sealed record RouteDto(
    RouteId Id,
    string OriginAddress,
    string OriginCoordinates,
    string DestinationAddress,
    string DestinationCoordinates,
    RouteProvider Provider,
    MonitoringStatus MonitoringStatus,
    string? JobChainId,
    DateTimeOffset CreatedAt,
    IReadOnlyList<MonitoringWindowDto> Windows,
    RouteId? ReturnRouteId = null);

public sealed record MonitoringWindowDto(
    WindowId Id,
    string StartTime,
    string EndTime,
    IReadOnlyList<string> DaysOfWeek,
    bool IsActive);

public sealed record PagedResult<T>(
    int Page,
    int PageSize,
    int TotalCount,
    IReadOnlyList<T> Items);
