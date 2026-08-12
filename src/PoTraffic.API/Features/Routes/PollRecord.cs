using System.Text.Json.Serialization;

namespace PoTraffic.API.Features.Routes;

public sealed class PollRecord
{
    public PollRecordId Id { get; set; }
    public RouteId RouteId { get; set; }
    public SessionId? SessionId { get; set; }
    public DateTimeOffset PolledAt { get; set; }
    public int TravelDurationSeconds { get; set; }
    public int DistanceMetres { get; set; }
    public bool IsRerouted { get; set; }

    [JsonIgnore]
    public Route Route { get; set; } = null!;
    [JsonIgnore]
    public MonitoringSession? Session { get; set; }
}
