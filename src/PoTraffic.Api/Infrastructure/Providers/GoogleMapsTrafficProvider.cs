using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace PoTraffic.Api.Infrastructure.Providers;

// Strategy pattern — concrete GoogleMaps implementation of ITrafficProvider
public sealed class GoogleMapsTrafficProvider : ITrafficProvider
{
    private const string GeocodeBaseUrl = "https://maps.googleapis.com/maps/api/geocode/json";

    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<GoogleMapsTrafficProvider> _logger;

    public GoogleMapsTrafficProvider(
        HttpClient httpClient,
        IConfiguration configuration,
        ILogger<GoogleMapsTrafficProvider> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<string?> GeocodeAsync(string address, CancellationToken ct = default)
    {
        string? apiKey = _configuration["GoogleMaps:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.LogError("Google Maps API key is not configured (GoogleMaps:ApiKey).");
            return null;
        }

        string url = $"{GeocodeBaseUrl}?address={Uri.EscapeDataString(address)}&key={apiKey}";

        try
        {
            GoogleGeocodeResponse? response = await _httpClient
                .GetFromJsonAsync<GoogleGeocodeResponse>(url, ct);

            if (response?.Status == "OK" && response.Results.Length > 0)
            {
                GoogleLocation loc = response.Results[0].Geometry.Location;
                string coords = $"{loc.Lat},{loc.Lng}";
                _logger.LogDebug("Google Maps geocoded '{Address}' → {Coords}", address, coords);
                return coords;
            }

            _logger.LogWarning("Google Maps geocoding returned status '{Status}' for address '{Address}'.",
                response?.Status, address);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Google Maps geocoding request failed for address '{Address}'.", address);
            return null;
        }
    }

    public async Task<TravelResult?> GetTravelTimeAsync(
        string originCoordinates,
        string destinationCoordinates,
        CancellationToken ct = default)
    {
        string? apiKey = _configuration["GoogleMaps:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.LogError("Google Maps API key is not configured (GoogleMaps:ApiKey).");
            return null;
        }

        string url = $"https://maps.googleapis.com/maps/api/distancematrix/json"
            + $"?origins={Uri.EscapeDataString(originCoordinates)}"
            + $"&destinations={Uri.EscapeDataString(destinationCoordinates)}"
            + $"&departure_time=now&traffic_model=best_guess&key={apiKey}";

        try
        {
            GoogleDistanceMatrixResponse? response =
                await _httpClient.GetFromJsonAsync<GoogleDistanceMatrixResponse>(url, ct);

            GoogleElement? element = response?.Rows?.FirstOrDefault()?.Elements?.FirstOrDefault();
            if (element?.Status != "OK")
            {
                _logger.LogWarning(
                    "Google Distance Matrix returned status '{Status}' for {Origin} → {Dest}.",
                    element?.Status ?? response?.Status, originCoordinates, destinationCoordinates);
                return null;
            }

            int duration  = element.DurationInTraffic?.Value ?? element.Duration.Value;
            int distance  = element.Distance.Value;
            string rawJson = System.Text.Json.JsonSerializer.Serialize(response);

            _logger.LogDebug(
                "Google Distance Matrix: {Origin} → {Dest} = {Duration}s / {Distance}m",
                originCoordinates, destinationCoordinates, duration, distance);

            return new TravelResult(duration, distance, rawJson);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Google Distance Matrix request failed for {Origin} → {Dest}.",
                originCoordinates, destinationCoordinates);
            return null;
        }
    }

    // ── Response projection types (Google Distance Matrix API) ─────────────────

    private sealed record GoogleDistanceMatrixResponse(
        [property: JsonPropertyName("status")]  string  Status,
        [property: JsonPropertyName("rows")]    GoogleRow[] Rows);

    private sealed record GoogleRow(
        [property: JsonPropertyName("elements")] GoogleElement[] Elements);

    private sealed record GoogleElement(
        [property: JsonPropertyName("status")]               string        Status,
        [property: JsonPropertyName("duration")]             GoogleValue   Duration,
        [property: JsonPropertyName("duration_in_traffic")] GoogleValue?  DurationInTraffic,
        [property: JsonPropertyName("distance")]             GoogleValue   Distance);

    private sealed record GoogleValue(
        [property: JsonPropertyName("value")] int Value);

    // ── Response projection types (Google Geocoding API v3) ───────────────────

    private sealed record GoogleGeocodeResponse(
        [property: JsonPropertyName("status")]  string Status,
        [property: JsonPropertyName("results")] GoogleGeocodeResult[] Results);

    private sealed record GoogleGeocodeResult(
        [property: JsonPropertyName("geometry")] GoogleGeometry Geometry);

    private sealed record GoogleGeometry(
        [property: JsonPropertyName("location")] GoogleLocation Location);

    private sealed record GoogleLocation(
        [property: JsonPropertyName("lat")] double Lat,
        [property: JsonPropertyName("lng")] double Lng);
}
