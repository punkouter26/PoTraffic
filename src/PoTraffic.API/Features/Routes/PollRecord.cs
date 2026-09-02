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

    /// <summary>
    /// Conditions at the origin when this sample was taken — one of
    /// <c>WeatherConditions.All</c>. Null when the feed was unavailable, disabled, or the
    /// sample predates weather capture; every consumer must treat null as "unknown" and
    /// exclude it rather than lumping it in with Clear.
    /// </summary>
    public string? WeatherCondition { get; set; }

    /// <summary>Temperature in °C at the origin, or null alongside a null condition.</summary>
    public double? TemperatureC { get; set; }

    /// <summary>Precipitation in mm at the origin, or null alongside a null condition.</summary>
    public double? PrecipitationMm { get; set; }

    [JsonIgnore]
    public Route Route { get; set; } = null!;
    [JsonIgnore]
    public MonitoringSession? Session { get; set; }
}
