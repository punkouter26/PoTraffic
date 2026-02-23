namespace PoTraffic.Api.Features.Routes.Entities;

public sealed class TripleTestSession
{
    public Guid Id { get; set; }
    public string OriginAddress { get; set; } = string.Empty;
    public string OriginCoordinates { get; set; } = string.Empty;
    public string DestinationAddress { get; set; } = string.Empty;
    public string DestinationCoordinates { get; set; } = string.Empty;

    /// <summary>0 = GoogleMaps, 1 = TomTom</summary>
    public int Provider { get; set; }

    public DateTimeOffset ScheduledAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public ICollection<TripleTestShot> Shots { get; set; } = new List<TripleTestShot>();
}
