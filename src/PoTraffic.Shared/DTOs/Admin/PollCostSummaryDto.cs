using PoTraffic.Shared.Enums;

namespace PoTraffic.Shared.DTOs.Admin;

/// <summary>Per-provider poll cost summary for the current UTC day.</summary>
public sealed record PollCostSummaryDto(
    DateTimeOffset AsOfUtc,
    RouteProvider Provider,
    int TotalPollCount,
    double TotalEstimatedCostUsd);
