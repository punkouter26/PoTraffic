namespace PoTraffic.Api.Infrastructure.Data.Projections;

/// <summary>
/// Keyless projection used by raw-SQL admin daily-usage queries.
/// </summary>
public sealed class UserDailyUsageDto
{
    public Guid UserId { get; set; }
    public string Email { get; set; } = string.Empty;
    public string? Provider { get; set; }
    public int TodayPollCount { get; set; }
}
