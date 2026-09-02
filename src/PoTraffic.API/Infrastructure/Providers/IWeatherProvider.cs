namespace PoTraffic.API.Infrastructure.Providers;

/// <summary>
/// Conditions at a coordinate at the moment of a poll. Recorded alongside each
/// <c>PollRecord</c> so a route's baseline can be split by weather — the single
/// largest explainable source of commute variance.
/// </summary>
public sealed record WeatherObservation(
    string Condition,
    double TemperatureC,
    double PrecipitationMm,
    int WeatherCode);

/// <summary>
/// Strategy for the conditions feed. Deliberately separate from
/// <see cref="ITrafficProvider"/>: weather is not per-route-provider, and a weather
/// failure must never fail a poll.
/// </summary>
public interface IWeatherProvider
{
    /// <summary>
    /// Conditions at <paramref name="coordinates"/> ("lat,lon") right now, or null when
    /// unavailable. Implementations swallow their own transport errors and return null —
    /// callers store "no weather for this sample" rather than dropping the sample.
    /// </summary>
    Task<WeatherObservation?> GetCurrentAsync(string coordinates, CancellationToken ct = default);
}

/// <summary>
/// The condition buckets a baseline is split by. Deliberately coarse: WMO publishes
/// ~28 distinct codes, but a commuter's question is "does rain cost me time", not
/// "does light intermittent drizzle cost me time" — and fine buckets would slice a
/// route's history into groups too small to mean anything.
/// </summary>
public static class WeatherConditions
{
    public const string Clear = "Clear";
    public const string Cloudy = "Cloudy";
    public const string Fog = "Fog";
    public const string Rain = "Rain";
    public const string Snow = "Snow";
    public const string Storm = "Storm";

    /// <summary>Display order — driest and calmest first, so a chart reads left to right.</summary>
    public static readonly string[] All = [Clear, Cloudy, Fog, Rain, Snow, Storm];

    /// <summary>
    /// Position of <paramref name="condition"/> in <see cref="All"/>, for stable sorting.
    /// An unrecognised bucket sorts last rather than first, so a future condition added to
    /// the enum but not to this list cannot silently displace Clear at the top of a card.
    /// </summary>
    public static int Order(string condition)
    {
        int index = Array.IndexOf(All, condition);
        return index < 0 ? All.Length : index;
    }

    /// <summary>
    /// Buckets a WMO 4677 weather code (what Open-Meteo returns) into one of <see cref="All"/>.
    /// Unknown codes fall to <see cref="Cloudy"/> — the neutral bucket, so an unrecognised
    /// code never invents a dramatic-looking Snow or Storm group.
    /// </summary>
    public static string FromWmoCode(int code) => code switch
    {
        0 => Clear,
        1 or 2 or 3 => Cloudy,
        45 or 48 => Fog,
        // 51–57 drizzle and freezing drizzle, 61–67 rain, 80–82 rain showers.
        >= 51 and <= 67 => Rain,
        >= 80 and <= 82 => Rain,
        // 71–77 snow and snow grains, 85–86 snow showers.
        >= 71 and <= 77 => Snow,
        85 or 86 => Snow,
        >= 95 and <= 99 => Storm,
        _ => Cloudy,
    };
}
