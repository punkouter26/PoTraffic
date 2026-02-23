namespace PoTraffic.Api.Features.Routes.Entities;

public sealed class PollRecord
{
    public Guid Id { get; set; }
    public Guid RouteId { get; set; }
    public Guid? SessionId { get; set; }
    public DateTimeOffset PolledAt { get; set; }
    public int TravelDurationSeconds { get; set; }
    public int DistanceMetres { get; set; }
    public bool IsRerouted { get; set; }
    public bool IsDeleted { get; set; }
    public string? RawProviderResponse { get; set; }

    public Route Route { get; set; } = null!;
    public MonitoringSession? Session { get; set; }
}
