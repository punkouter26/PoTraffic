using System.Text.Json.Serialization;

namespace PoTraffic.API.Features.Routes;

public sealed class Route
{
    public RouteId Id { get; set; }
    public UserId UserId { get; set; }
    public string OriginAddress { get; set; } = string.Empty;
    public string OriginCoordinates { get; set; } = string.Empty;
    public string DestinationAddress { get; set; } = string.Empty;
    public string DestinationCoordinates { get; set; } = string.Empty;

    /// <summary>0 = GoogleMaps, 1 = TomTom</summary>
    public int Provider { get; set; }

    /// <summary>0 = Active, 1 = Paused, 2 = Deleted</summary>
    public int MonitoringStatus { get; set; }

    public string? JobChainId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Links this route to its reverse-direction counterpart (#3). Set on both
    /// routes when a return trip is created; null for standalone routes.</summary>
    public RouteId? ReturnRouteId { get; set; }

    [JsonIgnore]
    public User User { get; set; } = null!;
    [JsonIgnore]
    public ICollection<MonitoringWindow> Windows { get; set; } = new List<MonitoringWindow>();
    [JsonIgnore]
    public ICollection<MonitoringSession> Sessions { get; set; } = new List<MonitoringSession>();
    [JsonIgnore]
    public ICollection<PollRecord> PollRecords { get; set; } = new List<PollRecord>();
}
