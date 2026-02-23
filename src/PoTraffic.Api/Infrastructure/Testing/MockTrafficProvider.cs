using PoTraffic.Api.Infrastructure.Providers;

namespace PoTraffic.Api.Infrastructure.Testing;

/// <summary>
/// Mock implementation of <see cref="ITrafficProvider"/> used for E2E and integration tests.
/// Returns synthetic travel data to avoid hitting paid production APIs (Google/TomTom)
/// and to enable deterministic test scenarios without requiring API keys.
/// </summary>
public sealed class MockTrafficProvider : ITrafficProvider
{
    private static readonly Random _rnd = new();

    public Task<string?> GeocodeAsync(string address, CancellationToken ct = default)
    {
        // Deterministic mock coords for addresses starting with 'Mock'
        if (address.StartsWith("Mock", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult<string?>("34.0522,-118.2437");

        // Generic mock for testing
        return Task.FromResult<string?>("34.1478,-118.1445");
    }

    public Task<TravelResult?> GetTravelTimeAsync(
        string originCoordinates,
        string destinationCoordinates,
        CancellationToken ct = default)
    {
        // Return a randomized but plausible travel result (15-45 minutes)
        int duration = _rnd.Next(900, 2700);
        int distance = _rnd.Next(5000, 15000);

        return Task.FromResult<TravelResult?>(new TravelResult(
            DurationSeconds: duration,
            DistanceMetres: distance,
            RawJson: "{\"status\":\"OK\", \"mock\": true}"
        ));
    }
}
