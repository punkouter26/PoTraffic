namespace PoTraffic.Shared.Constants;

/// <summary>
/// Canonical error codes returned by route creation/update flows so the client
/// can disambiguate causes (server misconfiguration vs. bad user input).
/// </summary>
public static class RouteErrorCodes
{
    /// <summary>The mapping/geocoding API key is not configured on the server.</summary>
    public const string MapsKeyMissing = "MAPS_KEY_MISSING";

    /// <summary>The address could not be resolved to coordinates by the provider.</summary>
    public const string AddressNotFound = "ADDRESS_NOT_FOUND";

    /// <summary>Origin and destination resolved to the same coordinates.</summary>
    public const string SameCoordinates = "SAME_COORDINATES";
}

/// <summary>
/// Thrown by an <c>ITrafficProvider</c> when geocoding cannot proceed because the
/// provider is not configured (e.g. missing API key). Distinct from an
/// unresolvable address, which is signalled by a null geocode result.
/// </summary>
public sealed class GeocodingConfigurationException : Exception
{
    public GeocodingConfigurationException(string message) : base(message) { }
}
