using PoTraffic.Api.Infrastructure.Providers;

namespace PoTraffic.IntegrationTests.Helpers;

/// <summary>
/// Test Double (Stub) — returns deterministic geocode coordinates and travel results
/// so integration tests can create routes without hitting real provider APIs.
/// Keyed-service replacement: registered for both GoogleMaps and TomTom in the test host.
/// </summary>
public sealed class FakeTrafficProvider : ITrafficProvider
{
    // Deterministic coordinates — slightly different for every call
    // so that origin/destination never collide (SAME_COORDINATES guard).
    private int _callCount;

    public Task<string?> GeocodeAsync(string address, CancellationToken ct = default)
    {
        int n = Interlocked.Increment(ref _callCount);
        // Return lat,lon based on call count to guarantee unique pairs
        string coordinates = $"51.5{n:D3},-0.1{n:D3}";
        return Task.FromResult<string?>(coordinates);
    }

    public Task<TravelResult?> GetTravelTimeAsync(
        string originCoordinates,
        string destinationCoordinates,
        CancellationToken ct = default)
    {
        return Task.FromResult<TravelResult?>(
            new TravelResult(DurationSeconds: 1200, DistanceMetres: 8000, RawJson: "{\"fake\":true}"));
    }
}
