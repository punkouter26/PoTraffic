namespace PoTraffic.Shared.DTOs.Routes;

/// <summary>An address autocomplete suggestion (#9). <see cref="Description"/> is the
/// human-readable address used to fill the input; the existing create flow geocodes it.</summary>
public sealed record PlaceSuggestionDto(string Description, string PlaceId);
