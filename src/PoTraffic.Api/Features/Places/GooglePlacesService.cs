using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using PoTraffic.Api.Infrastructure.Resilience;
using PoTraffic.Shared.DTOs.Routes;

namespace PoTraffic.Api.Features.Places;

/// <summary>
/// BFF proxy to the Google Places Autocomplete API (#9). The Google key stays server-side
/// (reuses <c>GoogleMaps:ApiKey</c>); the client only ever sees address suggestions. Failures
/// and a missing key degrade gracefully to an empty list so free-text entry still works.
/// </summary>
public sealed class GooglePlacesService(
    HttpClient http,
    IConfiguration config,
    ILogger<GooglePlacesService> logger)
{
    private const string AutocompleteUrl = "https://maps.googleapis.com/maps/api/place/autocomplete/json";
    private const int MinInputLength = 3;

    public async Task<IReadOnlyList<PlaceSuggestionDto>> AutocompleteAsync(string? input, CancellationToken ct)
    {
        string? key = config["GoogleMaps:ApiKey"];
        if (string.IsNullOrWhiteSpace(key) || input is null || input.Trim().Length < MinInputLength)
            return [];

        string url = $"{AutocompleteUrl}?input={Uri.EscapeDataString(input.Trim())}&types=address&key={key}";
        try
        {
            PlacesResponse? resp = await http.GetFromJsonAsync<PlacesResponse>(url, ct);
            if (resp?.Status is not ("OK" or "ZERO_RESULTS"))
            {
                logger.LogWarning("Places autocomplete returned status '{Status}'.", resp?.Status);
                return [];
            }
            return resp.Predictions
                .Select(p => new PlaceSuggestionDto(p.Description, p.PlaceId))
                .ToList();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Places autocomplete request failed.");
            return [];
        }
    }

    private sealed record PlacesResponse(
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("predictions")] Prediction[] Predictions);

    private sealed record Prediction(
        [property: JsonPropertyName("description")] string Description,
        [property: JsonPropertyName("place_id")] string PlaceId);
}

public static class PlacesServiceExtensions
{
    internal static IServiceCollection AddPlacesServices(this IServiceCollection services)
    {
        services.AddHttpClient<GooglePlacesService>()
            .AddResilienceHandler(ResiliencePipelineExtensions.TrafficPipeline);
        return services;
    }

    public static IEndpointRouteBuilder MapPlacesEndpoints(this IEndpointRouteBuilder app)
    {
        // GET /api/places/autocomplete?q=<partial address> — BFF-proxied suggestions (#9)
        app.MapGet("/api/places/autocomplete", async (
                string? q, GooglePlacesService places, CancellationToken ct) =>
                Results.Ok(await places.AutocompleteAsync(q, ct)))
            .RequireAuthorization("ProductionMicrosoftAuth")
            .WithTags("Places");
        return app;
    }
}
