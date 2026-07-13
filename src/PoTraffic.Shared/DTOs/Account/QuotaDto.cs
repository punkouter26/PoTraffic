namespace PoTraffic.Shared.DTOs.Account;

public sealed record QuotaDto(
    int DailyLimit,
    int UsedToday,
    int Remaining,
    DateTimeOffset ResetsAtUtc,
    int PollsToday = 0,
    decimal EstimatedCostTodayUsd = 0,
    decimal ProjectedMonthlyCostUsd = 0);
