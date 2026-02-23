using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace PoTraffic.Api.Infrastructure.Providers;

// Strategy pattern — concrete TomTom/OpenStreetMap implementation of ITrafficProvider.
// Geocoding is backed by Nominatim (OpenStreetMap) — free, no API key required.
public sealed class TomTomTrafficProvider : ITrafficProvider
{
    private const string NominatimUrl = "https://nominatim.openstreetmap.org/search";
    // Nominatim usage policy requires a descriptive User-Agent identifying the application.
    private const string UserAgent = "PoTraffic/1.0 (commute-monitoring; contact@potraffic.dev)";

    private readonly HttpClient _httpClient;
    private readonly ILogger<TomTomTrafficProvider> _logger;

    public TomTomTrafficProvider(
        HttpClient httpClient,
        ILogger<TomTomTrafficProvider> logger)
    {
        _httpClient = httpClient;
        _logger = logger;

        // Nominatim requires a User-Agent on every request.
        if (!_httpClient.DefaultRequestHeaders.Contains("User-Agent"))
            _httpClient.DefaultRequestHeaders.Add("User-Agent", UserAgent);
    }

    public async Task<string?> GeocodeAsync(string address, CancellationToken ct = default)
    {
        string url = $"{NominatimUrl}?q={Uri.EscapeDataString(address)}&format=json&limit=1";

        try
        {
            NominatimResult[]? results = await _httpClient
                .GetFromJsonAsync<NominatimResult[]>(url, ct);

            if (results is { Length: > 0 })
            {
                string coords = $"{results[0].Lat},{results[0].Lon}";
                _logger.LogDebug("Nominatim geocoded '{Address}' → {Coords}", address, coords);
                return coords;
            }

            _logger.LogWarning("Nominatim returned no results for address '{Address}'.", address);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Nominatim geocoding request failed for address '{Address}'.", address);
            return null;
        }
    }

    public async Task<TravelResult?> GetTravelTimeAsync(
        string originCoordinates,
        string destinationCoordinates,
        CancellationToken ct = default)
    {
        // Uses OSRM public routing API — free, no key required.
        // Coordinates from Nominatim geocoding are "lat,lon"; OSRM expects "lon,lat".
        static string ToOsrm(string latLon)
        {
            string[] parts = latLon.Split(',');
            return parts.Length == 2 ? $"{parts[1].Trim()},{parts[0].Trim()}" : latLon;
        }

        string origin = ToOsrm(originCoordinates);
        string dest   = ToOsrm(destinationCoordinates);
        string url    = $"https://router.project-osrm.org/route/v1/driving/{origin};{dest}?overview=false&annotations=false";

        try
        {
            OsrmRouteResponse? response =
                await _httpClient.GetFromJsonAsync<OsrmRouteResponse>(url, ct);

            OsrmRoute? route = response?.Routes?.FirstOrDefault();
            if (route is null || response?.Code != "Ok")
            {
                _logger.LogWarning(
                    "OSRM returned code '{Code}' for {Origin} → {Dest}.",
                    response?.Code, originCoordinates, destinationCoordinates);
                return null;
            }

            int duration = (int)Math.Round(route.Duration);
            int distance = (int)Math.Round(route.Distance);
            string rawJson = System.Text.Json.JsonSerializer.Serialize(response);

            _logger.LogDebug(
                "OSRM: {Origin} → {Dest} = {Duration}s / {Distance}m",
                originCoordinates, destinationCoordinates, duration, distance);

            return new TravelResult(duration, distance, rawJson);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "OSRM routing request failed for {Origin} → {Dest}.",
                originCoordinates, destinationCoordinates);
            return null;
        }
    }

    // ── Response projection types (OSRM Routing API) ─────────────────────────

    private sealed record OsrmRouteResponse(
        [property: JsonPropertyName("code")]   string      Code,
        [property: JsonPropertyName("routes")] OsrmRoute[] Routes);

    private sealed record OsrmRoute(
        [property: JsonPropertyName("duration")] double Duration,
        [property: JsonPropertyName("distance")] double Distance);

    // ── Response projection type (Nominatim Search API) ───────────────────────

    private sealed record NominatimResult(
        [property: JsonPropertyName("lat")] string Lat,
        [property: JsonPropertyName("lon")] string Lon);
}
