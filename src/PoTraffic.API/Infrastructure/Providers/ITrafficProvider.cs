namespace PoTraffic.API.Infrastructure.Providers;

// Strategy pattern — swaps traffic data source per route provider setting
public interface ITrafficProvider
{
    /// <summary>Returns geocoded coordinates "lat,lon" for the given address, or null if unresolvable.</summary>
    Task<string?> GeocodeAsync(string address, CancellationToken ct = default);

    /// <summary>Returns real-time travel result between two coordinate pairs, or null on provider error.</summary>
    Task<TravelResult?> GetTravelTimeAsync(
        string originCoordinates,
        string destinationCoordinates,
        CancellationToken ct = default);

    /// <summary>
    /// Returns the drivable road shape between two coordinate pairs, or null when this
    /// provider cannot supply one.
    ///
    /// <para>Default is null so a provider that has no geometry product stays valid.
    /// Callers must degrade to a straight line rather than treat null as an error —
    /// the shape is decoration on a page whose real content is the timing data.</para>
    ///
    /// <para>The result is expected to be cached by the caller and reused: the roads
    /// between two fixed addresses do not change between probes, so this should cost
    /// one provider call per route for the life of the route.</para>
    /// </summary>
    Task<RouteGeometry?> GetRouteGeometryAsync(
        string originCoordinates,
        string destinationCoordinates,
        CancellationToken ct = default) => Task.FromResult<RouteGeometry?>(null);
}

/// <summary>
/// A route's road shape as an encoded polyline (Google's algorithm, precision 5) —
/// the format Leaflet's decoder on the client expects.
/// </summary>
public sealed record RouteGeometry(string EncodedPolyline);

public sealed record TravelResult(
    int DurationSeconds,
    int DistanceMetres,
    string? RawJson);
