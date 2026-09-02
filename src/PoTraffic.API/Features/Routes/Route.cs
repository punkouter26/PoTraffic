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

    /// <summary>
    /// The demo route, whose history was generated rather than polled (#10). A new account
    /// has nothing to look at for days — every chart on this app needs accumulated samples
    /// — so one seeded route makes the product legible immediately.
    ///
    /// <para>A sample route is never polled and never charged: it is created Paused with no
    /// monitoring window, and its samples are excluded from quota and cost reporting. Both
    /// facts have to hold, or the demo silently spends the user's daily quota.</para>
    /// </summary>
    public bool IsSample { get; set; }

    /// <summary>
    /// The road shape between origin and destination, as a Google-encoded polyline.
    /// Fetched lazily the first time the route's map is opened and kept forever — the
    /// roads between two fixed addresses do not change between probes, so this is one
    /// provider call per route rather than one per map view.
    /// Null means "not fetched yet"; see <see cref="PathUnavailable"/> for "asked and
    /// the provider had nothing".
    /// </summary>
    public string? PathPolyline { get; set; }

    /// <summary>
    /// Set when a geometry fetch came back empty (provider not enabled for this key,
    /// no drivable route, request failed). Without this the app would re-ask the
    /// provider on every single map view for a route that will never have a shape.
    /// The map draws a straight line in that case.
    /// </summary>
    public DateTimeOffset? PathUnavailableAt { get; set; }

    [JsonIgnore]
    public User User { get; set; } = null!;
    [JsonIgnore]
    public ICollection<MonitoringWindow> Windows { get; set; } = new List<MonitoringWindow>();
    [JsonIgnore]
    public ICollection<MonitoringSession> Sessions { get; set; } = new List<MonitoringSession>();
    [JsonIgnore]
    public ICollection<PollRecord> PollRecords { get; set; } = new List<PollRecord>();
}
