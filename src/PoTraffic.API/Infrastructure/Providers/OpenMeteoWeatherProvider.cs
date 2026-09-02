using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Logging;

namespace PoTraffic.API.Infrastructure.Providers;

/// <summary>
/// Conditions from Open-Meteo. Chosen over the keyed weather APIs because it needs no
/// API key and no Key Vault secret, which keeps weather from becoming another thing that
/// can be misconfigured into a broken deploy — a route still polls fine with no weather.
/// </summary>
public sealed class OpenMeteoWeatherProvider : IWeatherProvider
{
    private const string BaseUrl = "https://api.open-meteo.com/v1/forecast";

    /// <summary>
    /// Weather moves far slower than the 5-minute poll cadence, and every route sharing a
    /// neighbourhood shares an answer, so observations are cached by rounded coordinate.
    /// Two decimal places is ~1km — the same weather by any useful definition.
    /// </summary>
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(10);

    private readonly HttpClient _httpClient;
    private readonly HybridCache _cache;
    private readonly ILogger<OpenMeteoWeatherProvider> _logger;

    public OpenMeteoWeatherProvider(
        HttpClient httpClient,
        HybridCache cache,
        ILogger<OpenMeteoWeatherProvider> logger)
    {
        _httpClient = httpClient;
        _cache = cache;
        _logger = logger;
    }

    public async Task<WeatherObservation?> GetCurrentAsync(string coordinates, CancellationToken ct = default)
    {
        if (!TryParseCoordinates(coordinates, out double lat, out double lon))
        {
            _logger.LogWarning("Open-Meteo: unparseable coordinates '{Coordinates}' — no weather recorded.", coordinates);
            return null;
        }

        string cacheKey = $"weather:{lat.ToString("F2", CultureInfo.InvariantCulture)},{lon.ToString("F2", CultureInfo.InvariantCulture)}";

        // GetOrCreateAsync surfaces the factory's exception to every caller waiting on the
        // same key, so the fetch swallows its own failures and caches "no observation".
        // A null result is cached deliberately: a provider that is down stays down for the
        // next few minutes, and hammering it once per poll per route helps nobody.
        return await _cache.GetOrCreateAsync(
            cacheKey,
            (Provider: this, Lat: lat, Lon: lon),
            static (state, token) => state.Provider.FetchAsync(state.Lat, state.Lon, token),
            new HybridCacheEntryOptions { Expiration = CacheFor, LocalCacheExpiration = CacheFor },
            cancellationToken: ct);
    }

    private async ValueTask<WeatherObservation?> FetchAsync(double lat, double lon, CancellationToken ct)
    {
        string url = string.Create(CultureInfo.InvariantCulture,
            $"{BaseUrl}?latitude={lat:F4}&longitude={lon:F4}&current=temperature_2m,precipitation,weather_code");

        try
        {
            OpenMeteoResponse? response = await _httpClient.GetFromJsonAsync<OpenMeteoResponse>(url, ct);
            if (response?.Current is not { } current)
            {
                _logger.LogWarning("Open-Meteo returned no current block for {Lat},{Lon}.", lat, lon);
                return null;
            }

            return new WeatherObservation(
                Condition: WeatherConditions.FromWmoCode(current.WeatherCode),
                TemperatureC: current.Temperature,
                PrecipitationMm: current.Precipitation,
                WeatherCode: current.WeatherCode);
        }
        catch (Exception ex)
        {
            // Weather is supplementary. Log and return null so the poll it decorates still lands.
            _logger.LogWarning(ex, "Open-Meteo request failed for {Lat},{Lon} — poll recorded without weather.", lat, lon);
            return null;
        }
    }

    /// <summary>Parses the "lat,lon" form every route stores its coordinates in.</summary>
    internal static bool TryParseCoordinates(string coordinates, out double lat, out double lon)
    {
        lat = 0;
        lon = 0;
        if (string.IsNullOrWhiteSpace(coordinates)) return false;

        string[] parts = coordinates.Split(',');
        return parts.Length == 2
            && double.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out lat)
            && double.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out lon);
    }

    private sealed record OpenMeteoResponse(
        [property: JsonPropertyName("current")] OpenMeteoCurrent? Current);

    private sealed record OpenMeteoCurrent(
        [property: JsonPropertyName("temperature_2m")] double Temperature,
        [property: JsonPropertyName("precipitation")] double Precipitation,
        [property: JsonPropertyName("weather_code")] int WeatherCode);
}
