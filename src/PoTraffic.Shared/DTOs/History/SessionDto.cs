using PoTraffic.Shared.Enums;

namespace PoTraffic.Shared.DTOs.History;

public sealed record SessionDto(
    SessionId Id,
    RouteId RouteId,
    DateOnly SessionDate,
    SessionState State,
    DateTimeOffset? FirstPollAt,
    DateTimeOffset? LastPollAt,
    int PollCount,
    int QuotaConsumed,
    bool IsHolidayExcluded);
