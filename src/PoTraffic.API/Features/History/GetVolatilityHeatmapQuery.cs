using System.Globalization;
using PoTraffic.API.Infrastructure.Storage;
using PoTraffic.Shared.DTOs.History;

namespace PoTraffic.API.Features.History;

public sealed record GetVolatilityHeatmapQuery(RouteId RouteId, UserId UserId)
    : IRequest<VolatilityHeatmapDto>;

/// <summary>
/// Aggregates a route's whole history into a day-of-week × half-hour grid (#5), so
/// "Tuesday at 17:30 is the worst 30 minutes of your week" is one glance rather than
/// seven baseline reads.
///
/// <para>
/// The reference point is the <em>median</em> sample, not the mean: a handful of 3× outlier
/// commutes drags a mean upward far enough that genuinely congested cells stop looking
/// congested relative to it. Day-of-week and half-hour are bucketed in the user's local
/// time zone (derived from their <c>User.Locale</c>), so a 17:30 row reads as the rush
/// hour the user actually drives in.
/// </para>
/// </summary>
public sealed class GetVolatilityHeatmapQueryHandler(TableStorageContext db)
    : IRequestHandler<GetVolatilityHeatmapQuery, VolatilityHeatmapDto>
{
    public Task<VolatilityHeatmapDto> Handle(GetVolatilityHeatmapQuery query, CancellationToken ct)
    {
        if (!db.OwnsRoute(query.RouteId, query.UserId))
            return Task.FromResult(new VolatilityHeatmapDto(query.RouteId, 0, 0, TimeZoneInfo.Utc.Id, []));

        List<PollRecord> polls = db.Polls
            .Where(p => p.RouteId == query.RouteId)
            .ToList();

        if (polls.Count == 0)
            return Task.FromResult(new VolatilityHeatmapDto(query.RouteId, 0, 0, TimeZoneInfo.Utc.Id, []));

        // Resolve the user's time zone from their profile locale. Falls back to UTC if
        // the locale has no well-known zone mapping — the heatmap is still meaningful
        // (just shifted relative to the user's wall clock).
        string locale = db.Users
            .Where(u => u.Id == query.UserId)
            .Select(u => u.Locale)
            .FirstOrDefault() ?? "en-US";
        TimeZoneInfo userZone = ResolveUserZone(locale);
        string zoneId = userZone.Id;

        List<HeatmapCellDto> cells = [.. polls
            .GroupBy(p =>
            {
                DateTimeOffset local = TimeZoneInfo.ConvertTime(p.PolledAt, userZone);
                return (
                    local.DayOfWeek,
                    local.Hour,
                    HalfHour: local.Minute >= 30 ? 1 : 0);
            })
            .Select(g =>
            {
                List<double> durations = [.. g.Select(p => (double)p.TravelDurationSeconds)];
                double mean = durations.Average();
                double stdDev = durations.Count > 1
                    ? Math.Sqrt(durations.Sum(d => Math.Pow(d - mean, 2)) / (durations.Count - 1))
                    : 0;

                return new HeatmapCellDto(
                    g.Key.DayOfWeek.ToString(),
                    g.Key.Hour,
                    g.Key.HalfHour,
                    mean,
                    stdDev,
                    durations.Count);
            })
            .OrderBy(c => c.Hour)
            .ThenBy(c => c.HalfHour)];

        return Task.FromResult(new VolatilityHeatmapDto(
            query.RouteId,
            Median([.. polls.Select(p => (double)p.TravelDurationSeconds)]),
            polls.Count,
            zoneId,
            cells));
    }

    /// <summary>
    /// Best-effort mapping from a BCP-47 locale (e.g. <c>en-US</c>, <c>de-DE</c>) to
    /// a <see cref="TimeZoneInfo"/>. Strategy:
    /// <list type="number">
    ///   <item><description>Try common Windows/IANA zone IDs associated with the locale's
    ///   region. en-US → US Eastern, en-GB → UK, de-DE → Berlin, ja-JP → Tokyo, etc.</description></item>
    ///   <item><description>Fall back to the system local zone.</description></item>
    ///   <item><description>Fall back to UTC.</description></item>
    /// </list>
    /// The fallback chain guarantees a non-null result so the heatmap is always renderable.
    /// </summary>
    internal static TimeZoneInfo ResolveUserZone(string locale)
    {
        string? region = TryGetRegion(locale);
        string[]? candidates = region is null ? null : LocaleToZones.TryGetValue(region, out string[]? v) ? v : null;
        if (candidates is not null)
        {
            foreach (string id in candidates)
            {
                try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
                catch (TimeZoneNotFoundException) { /* try next */ }
            }
        }
        try { return TimeZoneInfo.Local; }
        catch (Exception) { /* fall through */ }
        return TimeZoneInfo.Utc;
    }

    private static string? TryGetRegion(string locale)
    {
        if (string.IsNullOrWhiteSpace(locale)) return null;
        try
        {
            RegionInfo r = new(locale);
            return r.TwoLetterISORegionName;
        }
        catch (ArgumentException)
        {
            // Locale isn't a valid region tag — try splitting on '-' as a last resort.
            int dash = locale.IndexOf('-');
            return dash >= 0 && dash < locale.Length - 1
                ? locale[(dash + 1)..].ToUpperInvariant()
                : null;
        }
    }

    /// <summary>
    /// Region → ordered list of preferred zone IDs. The first ID that resolves on the
    /// current host wins. Each region gets a Windows-first entry followed by an IANA
    /// entry so the same code works on Windows and Linux containers.
    /// </summary>
    private static readonly Dictionary<string, string[]> LocaleToZones = new(StringComparer.OrdinalIgnoreCase)
    {
        ["US"] = ["Eastern Standard Time", "America/New_York"],
        ["CA"] = ["Eastern Standard Time", "America/Toronto"],
        ["GB"] = ["GMT Standard Time", "Europe/London"],
        ["UK"] = ["GMT Standard Time", "Europe/London"],
        ["IE"] = ["GMT Standard Time", "Europe/Dublin"],
        ["DE"] = ["W. Europe Standard Time", "Europe/Berlin"],
        ["FR"] = ["Romance Standard Time", "Europe/Paris"],
        ["ES"] = ["Romance Standard Time", "Europe/Madrid"],
        ["IT"] = ["Romance Standard Time", "Europe/Rome"],
        ["NL"] = ["W. Europe Standard Time", "Europe/Amsterdam"],
        ["PL"] = ["Central European Standard Time", "Europe/Warsaw"],
        ["SE"] = ["Central European Standard Time", "Europe/Stockholm"],
        ["NO"] = ["Central European Standard Time", "Europe/Oslo"],
        ["FI"] = ["FLE Standard Time", "Europe/Helsinki"],
        ["PT"] = ["GMT Standard Time", "Europe/Lisbon"],
        ["AU"] = ["AUS Eastern Standard Time", "Australia/Sydney"],
        ["NZ"] = ["New Zealand Standard Time", "Pacific/Auckland"],
        ["JP"] = ["Tokyo Standard Time", "Asia/Tokyo"],
        ["KR"] = ["Korea Standard Time", "Asia/Seoul"],
        ["CN"] = ["China Standard Time", "Asia/Shanghai"],
        ["HK"] = ["China Standard Time", "Asia/Hong_Kong"],
        ["SG"] = ["Singapore Standard Time", "Asia/Singapore"],
        ["IN"] = ["India Standard Time", "Asia/Kolkata"],
        ["BR"] = ["E. South America Standard Time", "America/Sao_Paulo"],
        ["MX"] = ["Central Standard Time (Mexico)", "America/Mexico_City"],
        ["ZA"] = ["South Africa Standard Time", "Africa/Johannesburg"],
        ["AE"] = ["Arabian Standard Time", "Asia/Dubai"],
    };

    /// <summary>Middle value of <paramref name="values"/>; the mean of the middle pair when even.</summary>
    private static double Median(List<double> values)
    {
        values.Sort();
        int mid = values.Count / 2;
        return values.Count % 2 == 1
            ? values[mid]
            : (values[mid - 1] + values[mid]) / 2;
    }
}
