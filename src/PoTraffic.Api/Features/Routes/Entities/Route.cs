namespace PoTraffic.Api.Features.Routes.Entities;

public sealed class Route
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string OriginAddress { get; set; } = string.Empty;
    public string OriginCoordinates { get; set; } = string.Empty;
    public string DestinationAddress { get; set; } = string.Empty;
    public string DestinationCoordinates { get; set; } = string.Empty;

    /// <summary>0 = GoogleMaps, 1 = TomTom</summary>
    public int Provider { get; set; }

    /// <summary>0 = Active, 1 = Paused, 2 = Deleted</summary>
    public int MonitoringStatus { get; set; }

    public string? HangfireJobChainId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public User User { get; set; } = null!;
    public ICollection<MonitoringWindow> Windows { get; set; } = new List<MonitoringWindow>();
    public ICollection<MonitoringSession> Sessions { get; set; } = new List<MonitoringSession>();
    public ICollection<PollRecord> PollRecords { get; set; } = new List<PollRecord>();
}
