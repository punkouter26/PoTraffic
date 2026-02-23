using PoTraffic.Shared.Enums;

namespace PoTraffic.Shared.DTOs.Routes;

public sealed record RouteDto(
    Guid Id,
    string OriginAddress,
    string OriginCoordinates,
    string DestinationAddress,
    string DestinationCoordinates,
    RouteProvider Provider,
    MonitoringStatus MonitoringStatus,
    string? HangfireJobChainId,
    DateTimeOffset CreatedAt,
    IReadOnlyList<MonitoringWindowDto> Windows);

public sealed record MonitoringWindowDto(
    Guid Id,
    string StartTime,
    string EndTime,
    IReadOnlyList<string> DaysOfWeek,
    bool IsActive);

public sealed record PagedResult<T>(
    int Page,
    int PageSize,
    int TotalCount,
    IReadOnlyList<T> Items);
