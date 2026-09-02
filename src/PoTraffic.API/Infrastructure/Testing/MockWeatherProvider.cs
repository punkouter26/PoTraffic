using System.Security.Cryptography;
using System.Text;
using PoTraffic.API.Infrastructure.Providers;

namespace PoTraffic.API.Infrastructure.Testing;

/// <summary>
/// Deterministic stand-in for <see cref="IWeatherProvider"/> in Testing and mock-provider
/// runs. Derives the condition from the coordinate and the current hour, so a test can
/// assert on a stable answer while a long local session still sees the conditions change
/// over the day — a provider that always says "Clear" would make the weather split on the
/// route page look broken rather than empty.
/// </summary>
public sealed class MockWeatherProvider : IWeatherProvider
{
    public Task<WeatherObservation?> GetCurrentAsync(string coordinates, CancellationToken ct = default)
    {
        byte[] hash = SHA256.HashData(
            Encoding.UTF8.GetBytes($"{coordinates}|{DateTimeOffset.UtcNow:yyyy-MM-ddTHH}"));

        // WMO codes, one per bucket, so every branch of WeatherConditions.FromWmoCode is
        // reachable from a mock run.
        int[] codes = [0, 3, 45, 63, 73, 95];
        int code = codes[hash[0] % codes.Length];
        string condition = WeatherConditions.FromWmoCode(code);

        return Task.FromResult<WeatherObservation?>(new WeatherObservation(
            Condition: condition,
            TemperatureC: Math.Round(-5 + (hash[1] / 255.0 * 35), 1),
            PrecipitationMm: condition is WeatherConditions.Clear or WeatherConditions.Cloudy
                ? 0
                : Math.Round(hash[2] / 255.0 * 8, 1),
            WeatherCode: code));
    }
}
