using PoTraffic.Shared.Enums;

namespace PoTraffic.Shared.DTOs.Admin;

public sealed record TripleTestRequest(
    string OriginAddress,
    string DestinationAddress,
    RouteProvider Provider,
    DateTimeOffset? StartAt);

public sealed record TripleTestShotDto(
    int ShotIndex,
    int OffsetSeconds,
    DateTimeOffset? FiredAt,
    bool? IsSuccess,
    int? DurationSeconds,
    int? DistanceMetres,
    string? ErrorCode);

public sealed record TripleTestSessionDto(
    Guid SessionId,
    string OriginAddress,
    string DestinationAddress,
    RouteProvider Provider,
    DateTimeOffset ScheduledAt,
    IReadOnlyList<TripleTestShotDto> Shots,
    int? IdealShotIndex,
    double? AverageDurationSeconds,
    double? AverageDistanceMetres);
