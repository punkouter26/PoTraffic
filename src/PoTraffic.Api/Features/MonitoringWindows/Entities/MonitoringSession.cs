namespace PoTraffic.Api.Features.MonitoringWindows.Entities;

public sealed class MonitoringSession
{
    public Guid Id { get; set; }
    public Guid RouteId { get; set; }
    public DateOnly SessionDate { get; set; }

    /// <summary>0 = Pending, 1 = Active, 2 = Completed</summary>
    public int State { get; set; }

    public DateTimeOffset? FirstPollAt { get; set; }
    public DateTimeOffset? LastPollAt { get; set; }
    public int QuotaConsumed { get; set; }
    public int PollCount { get; set; }
    public bool IsHolidayExcluded { get; set; }

    public EntityRoute Route { get; set; } = null!;
    public ICollection<PollRecord> PollRecords { get; set; } = new List<PollRecord>();
}
