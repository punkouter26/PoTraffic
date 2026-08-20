using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using PoTraffic.API.Infrastructure.Logging;
using PoTraffic.Shared.Constants;

namespace PoTraffic.API.Infrastructure.Providers;

// Strategy pattern — concrete GoogleMaps implementation of ITrafficProvider
public sealed class GoogleMapsTrafficProvider : ITrafficProvider
{
    private const string GeocodeBaseUrl = "https://maps.googleapis.com/maps/api/geocode/json";
    private const string DirectionsBaseUrl = "https://maps.googleapis.com/maps/api/directions/json";

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
            // Server misconfiguration — distinct from an unresolvable address (null return).
            _logger.LogError("Google Maps API key is not configured (GoogleMaps:ApiKey).");
            throw new GeocodingConfigurationException(
                "Google Maps API key is not configured (GoogleMaps:ApiKey).");
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
                _logger.LogDebug("Google Maps geocoded {AddressRef} → {Coords}",
                    PiiRedactor.Redact(address), coords);
                return coords;
            }

            _logger.LogWarning("Google Maps geocoding returned status '{Status}' for address {AddressRef}.",
                response?.Status, PiiRedactor.Redact(address));
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Google Maps geocoding request failed for address {AddressRef}.",
                PiiRedactor.Redact(address));
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

            int duration = element.DurationInTraffic?.Value ?? element.Duration.Value;
            int distance = element.Distance.Value;

            // Deliberately re-serialised from the PROJECTION, not stored as the raw response
            // body: Google's payload carries origin_addresses/destination_addresses (formatted
            // street addresses), and this value is persisted on every PollRecord. Round-tripping
            // through the record type is what keeps that PII out of storage — do not "optimise"
            // this into reusing the response string.
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

    /// <summary>
    /// Fetches the road shape via the Directions API and returns its overview polyline.
    ///
    /// <para>This is a DIFFERENT Google product from the Distance Matrix call that drives
    /// polling, and a key that works for one may not have the other enabled. A denied or
    /// failed request is therefore not an error worth propagating: it returns null and the
    /// map falls back to a straight line between the endpoints. Polling is unaffected
    /// either way.</para>
    ///
    /// <para>No <c>departure_time</c> is sent. The caller stores the result permanently,
    /// so asking for a traffic-aware shape would bake one moment's detour into the route
    /// forever. The colouring on the map comes from this app's own samples instead.</para>
    /// </summary>
    public async Task<RouteGeometry?> GetRouteGeometryAsync(
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

        string url = $"{DirectionsBaseUrl}"
            + $"?origin={Uri.EscapeDataString(originCoordinates)}"
            + $"&destination={Uri.EscapeDataString(destinationCoordinates)}"
            + $"&key={apiKey}";

        try
        {
            GoogleDirectionsResponse? response =
                await _httpClient.GetFromJsonAsync<GoogleDirectionsResponse>(url, ct);

            string? points = response?.Routes?.FirstOrDefault()?.OverviewPolyline?.Points;
            if (response?.Status != "OK" || string.IsNullOrWhiteSpace(points))
            {
                _logger.LogInformation(
                    "Google Directions returned status '{Status}' for {Origin} → {Dest}; "
                    + "the map will draw an approximate straight line.",
                    response?.Status ?? "no response", originCoordinates, destinationCoordinates);
                return null;
            }

            _logger.LogDebug("Google Directions: geometry for {Origin} → {Dest} ({Length} chars).",
                originCoordinates, destinationCoordinates, points.Length);

            return new RouteGeometry(points);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Google Directions request failed for {Origin} → {Dest}; "
                + "the map will draw an approximate straight line.",
                originCoordinates, destinationCoordinates);
            return null;
        }
    }

    // ── Response projection types (Google Directions API) ─────────────────────

    private sealed record GoogleDirectionsResponse(
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("routes")] GoogleDirectionsRoute[]? Routes);

    private sealed record GoogleDirectionsRoute(
        [property: JsonPropertyName("overview_polyline")] GoogleOverviewPolyline? OverviewPolyline);

    private sealed record GoogleOverviewPolyline(
        [property: JsonPropertyName("points")] string Points);

    // ── Response projection types (Google Distance Matrix API) ─────────────────

    private sealed record GoogleDistanceMatrixResponse(
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("rows")] GoogleRow[] Rows);

    private sealed record GoogleRow(
        [property: JsonPropertyName("elements")] GoogleElement[] Elements);

    private sealed record GoogleElement(
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("duration")] GoogleValue Duration,
        [property: JsonPropertyName("duration_in_traffic")] GoogleValue? DurationInTraffic,
        [property: JsonPropertyName("distance")] GoogleValue Distance);

    private sealed record GoogleValue(
        [property: JsonPropertyName("value")] int Value);

    // ── Response projection types (Google Geocoding API v3) ───────────────────

    private sealed record GoogleGeocodeResponse(
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("results")] GoogleGeocodeResult[] Results);

    private sealed record GoogleGeocodeResult(
        [property: JsonPropertyName("geometry")] GoogleGeometry Geometry);

    private sealed record GoogleGeometry(
        [property: JsonPropertyName("location")] GoogleLocation Location);

    private sealed record GoogleLocation(
        [property: JsonPropertyName("lat")] double Lat,
        [property: JsonPropertyName("lng")] double Lng);
}
