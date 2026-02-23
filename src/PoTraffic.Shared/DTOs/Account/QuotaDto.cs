namespace PoTraffic.Shared.DTOs.Account;

public sealed record QuotaDto(
    int DailyLimit,
    int UsedToday,
    int Remaining,
    DateTimeOffset ResetsAtUtc);
