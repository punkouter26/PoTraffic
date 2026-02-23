using PoTraffic.Shared.Enums;

namespace PoTraffic.Shared.DTOs.Admin;

public sealed record UserDailyUsageDto(
    Guid UserId,
    string Email,
    string Locale,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastLoginAt,
    int TodayPollCount,
    double TodayEstimatedCostUsd,
    IReadOnlyList<ProviderBreakdownDto> ProviderBreakdown);

public sealed record ProviderBreakdownDto(
    RouteProvider Provider,
    int PollCount,
    double EstimatedCostUsd);
