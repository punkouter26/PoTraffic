namespace PoTraffic.Api.Infrastructure.Providers;

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
}

public sealed record TravelResult(
    int DurationSeconds,
    int DistanceMetres,
    string? RawJson);
