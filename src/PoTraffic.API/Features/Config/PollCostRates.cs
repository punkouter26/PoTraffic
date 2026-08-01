using System.Globalization;
using PoTraffic.API.Infrastructure.Storage;
using PoTraffic.Shared.Enums;

namespace PoTraffic.API.Features.Config;

/// <summary>
/// Per-poll provider rates read from <see cref="SystemConfiguration"/>, resolved once per query.
///
/// <para>
/// The config keys, the parse-with-fallback rule, and the provider→rate selection are defined
/// here only — the admin usage/cost queries and the per-user quota query all price polls through
/// this type, so a new provider or a renamed key is a single edit.
/// </para>
/// </summary>
public sealed class PollCostRates
{
    /// <summary>Prefix shared by every per-poll rate key — usable as a query filter.</summary>
    public const string KeyPrefix = "cost.perpoll.";

    public const string GoogleMapsKey = KeyPrefix + "googlemaps";
    public const string TomTomKey = KeyPrefix + "tomtom";

    private readonly decimal _googleMaps;
    private readonly decimal _tomTom;

    private PollCostRates(decimal googleMaps, decimal tomTom)
    {
        _googleMaps = googleMaps;
        _tomTom = tomTom;
    }

    /// <summary>The USD cost of a single poll against <paramref name="provider"/>.</summary>
    public decimal For(RouteProvider provider)
        => provider == RouteProvider.TomTom ? _tomTom : _googleMaps;

    /// <summary>
    /// Loads both rates in one pass over the configuration table. Fallbacks apply per rate
    /// when the key is absent or unparseable.
    /// </summary>
    public static PollCostRates Load(
        TableStorageContext db,
        decimal googleMapsFallback = 0m,
        decimal tomTomFallback = 0m)
    {
        Dictionary<string, string> configs = db.SystemConfigurations
            .Where(c => c.Key.StartsWith(KeyPrefix, StringComparison.Ordinal))
            .ToDictionary(c => c.Key, c => c.Value);

        return new PollCostRates(
            Resolve(configs, GoogleMapsKey, googleMapsFallback),
            Resolve(configs, TomTomKey, tomTomFallback));
    }

    private static decimal Resolve(Dictionary<string, string> configs, string key, decimal fallback)
        => configs.TryGetValue(key, out string? value)
           && decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal rate)
            ? rate
            : fallback;
}
