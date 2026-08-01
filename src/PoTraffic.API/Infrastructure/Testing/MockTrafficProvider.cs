using System.Security.Cryptography;
using System.Text;
using PoTraffic.API.Infrastructure.Providers;

namespace PoTraffic.API.Infrastructure.Testing;

/// <summary>
/// Mock implementation of <see cref="ITrafficProvider"/> used for E2E and integration tests.
/// Returns synthetic travel data to avoid hitting paid production APIs (Google/TomTom)
/// and to enable deterministic test scenarios without requiring API keys.
/// </summary>
public sealed class MockTrafficProvider : ITrafficProvider
{
    // Bounding box roughly covering greater Los Angeles, so mock coordinates stay
    // plausible and the distances between them are of a realistic magnitude.
    private const double MinLatitude = 33.70;
    private const double LatitudeSpan = 0.65;
    private const double MinLongitude = -118.70;
    private const double LongitudeSpan = 0.80;

    /// <summary>
    /// Derives coordinates from the address itself, so that:
    /// <list type="bullet">
    /// <item>different addresses always land on different points — geocoding every address
    /// to one shared point made origin and destination look identical and got route
    /// creation rejected with <c>SAME_COORDINATES</c>;</item>
    /// <item>the same address always lands on the same point, across calls and across
    /// processes, so a seeded route keeps its location on the next run.</item>
    /// </list>
    /// SHA-256 rather than <see cref="string.GetHashCode()"/> precisely because the latter
    /// is randomised per process and would break the second guarantee. This is a spreading
    /// function, not a security primitive.
    /// </summary>
    public Task<string?> GeocodeAsync(string address, CancellationToken ct = default)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(address.Trim().ToLowerInvariant()));

        double latitude = MinLatitude + (BitConverter.ToUInt32(hash, 0) / (double)uint.MaxValue * LatitudeSpan);
        double longitude = MinLongitude + (BitConverter.ToUInt32(hash, 4) / (double)uint.MaxValue * LongitudeSpan);

        // Invariant formatting — a culture with a comma decimal separator would produce
        // "34,12,-118,45" and make the coordinate pair unparseable.
        return Task.FromResult<string?>(FormattableString.Invariant($"{latitude:F6},{longitude:F6}"));
    }

    public Task<TravelResult?> GetTravelTimeAsync(
        string originCoordinates,
        string destinationCoordinates,
        CancellationToken ct = default)
    {
        // Random.Shared, not a shared Random instance: Random is not thread-safe, and
        // concurrent use silently degrades its internal state (returning zeros).
        int duration = Random.Shared.Next(900, 2700);   // 15-45 minutes
        int distance = Random.Shared.Next(5000, 15000);

        return Task.FromResult<TravelResult?>(new TravelResult(
            DurationSeconds: duration,
            DistanceMetres: distance,
            RawJson: "{\"status\":\"OK\", \"mock\": true}"
        ));
    }
}
