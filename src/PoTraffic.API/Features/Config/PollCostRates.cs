using System.Globalization;
using PoTraffic.API.Infrastructure.Storage;

namespace PoTraffic.API.Features.Config;

/// <summary>
/// The per-poll provider rate, read from <see cref="SystemConfiguration"/>. The admin usage
/// query and the per-user quota query both price polls through here, so they cannot disagree.
/// </summary>
public static class PollCostRates
{
    public const string GoogleMapsKey = "cost.perpoll.googlemaps";

    /// <summary>Seeded value; also used when the row is missing or unparseable.</summary>
    public const decimal GoogleMapsDefault = 0.005m;

    /// <summary>The USD cost of a single poll.</summary>
    public static decimal PerPoll(TableStorageContext db)
        => db.SystemConfigurations.FirstOrDefault(c => c.Key == GoogleMapsKey) is { } row
           && decimal.TryParse(row.Value, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal rate)
            ? rate
            : GoogleMapsDefault;
}
