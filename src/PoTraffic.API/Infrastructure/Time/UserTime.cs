using System.Globalization;
using PoTraffic.API.Infrastructure.Storage;

namespace PoTraffic.API.Infrastructure.Time;

/// <summary>
/// The user's wall clock. Every per-slot statistic — baseline, best departure, congestion
/// alert, map colour, heatmap, weather impact — buckets samples by <em>local</em> weekday and
/// time of day. Bucketing in UTC split an evening commute across two weekdays for anyone
/// west of Greenwich (17:00 PDT is already tomorrow in UTC) and moved every slot an hour at
/// each DST change, mixing two different local times into one baseline.
/// </summary>
public static class UserTime
{
    /// <summary>
    /// The zone the user's commute runs in: the browser-reported <see cref="User.TimeZoneId"/>,
    /// else a best guess from their locale, else UTC when the user is unknown.
    /// </summary>
    public static TimeZoneInfo ZoneFor(this TableStorageContext db, UserId userId)
    {
        User? user = db.Users.FirstOrDefault(u => u.Id == userId);
        if (user is null)
            return TimeZoneInfo.Utc;
        return TryFindZone(user.TimeZoneId) ?? ResolveUserZone(user.Locale);
    }

    /// <summary>The zone for an IANA or Windows id, or null when blank or unknown to this host.</summary>
    public static TimeZoneInfo? TryFindZone(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { return null; }
    }

    public static DateTimeOffset ToLocal(this DateTimeOffset instant, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(instant, zone);

    /// <summary>Quarter-hour of the day, 0–95.</summary>
    public static int QuarterOfDay(DateTimeOffset local) => (local.Hour * 4) + (local.Minute / 15);

    /// <summary>
    /// The instant a local wall-clock time happens. A time inside a spring-forward gap
    /// (02:30 on the night clocks jump to 03:00) does not exist, so it moves an hour later.
    /// </summary>
    public static DateTimeOffset ToUtc(DateTime localWallClock, TimeZoneInfo zone)
    {
        DateTime unspecified = DateTime.SpecifyKind(localWallClock, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(unspecified))
            unspecified = unspecified.AddHours(1);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(unspecified, zone), TimeSpan.Zero);
    }

    /// <summary>
    /// The route's samples that describe a usual day: everything except public holidays,
    /// whose empty roads would otherwise drag every baseline down.
    /// </summary>
    public static IEnumerable<PollRecord> UsualPolls(this TableStorageContext db, RouteId routeId) =>
        db.Polls.Where(p => p.RouteId == routeId && p.HolidayName == null);

    /// <summary>
    /// The route's usual samples in local time, narrowed to <paramref name="dayOfWeek"/> — or
    /// every day when that weekday is still too sparse to stand on its own (<c>FellBack</c>).
    /// Shared by the best-departure card and the departure plan so they rest on the same samples.
    /// </summary>
    public static (List<(DateTimeOffset Local, int Seconds)> Polls, bool FellBack) LocalPollsForDay(
        this TableStorageContext db, RouteId routeId, TimeZoneInfo zone, string dayOfWeek)
    {
        List<(DateTimeOffset Local, int Seconds)> all = db.UsualPolls(routeId)
            .Select(p => (p.PolledAt.ToLocal(zone), p.TravelDurationSeconds))
            .ToList();
        List<(DateTimeOffset Local, int Seconds)> day = Enum.TryParse(dayOfWeek, ignoreCase: true, out DayOfWeek dow)
            ? all.Where(p => p.Local.DayOfWeek == dow).ToList()
            : all;
        bool fellBack = day.Count < QuotaConstants.BaselineMinSessionCount;
        return (fellBack ? all : day, fellBack);
    }

    /// <summary>
    /// ISO country for holiday lookups, from the user's locale region (en-US → US).
    /// ponytail: locale is a proxy for where the commute is; reverse-geocode the route origin if
    /// users with a mismatched locale show up.
    /// </summary>
    public static string? CountryFor(this TableStorageContext db, UserId userId) =>
        TryGetRegion(db.Users.FirstOrDefault(u => u.Id == userId)?.Locale ?? string.Empty);

    /// <summary>
    /// Travel times of the route's samples that fell in the same local weekday and
    /// quarter-hour as <paramref name="instant"/>, filtered by <paramref name="include"/>.
    /// Shared by the congestion alert and the map colour so the two can never disagree
    /// about whether now is worse than usual.
    /// </summary>
    public static List<int> SameSlotDurations(
        this TableStorageContext db,
        RouteId routeId,
        DateTimeOffset instant,
        TimeZoneInfo zone,
        Func<PollRecord, bool> include)
    {
        DateTimeOffset local = instant.ToLocal(zone);
        int quarter = QuarterOfDay(local);
        return db.UsualPolls(routeId)
            .Where(include)
            .Where(p =>
            {
                DateTimeOffset l = p.PolledAt.ToLocal(zone);
                return l.DayOfWeek == local.DayOfWeek && QuarterOfDay(l) == quarter;
            })
            .Select(p => p.TravelDurationSeconds)
            .ToList();
    }

    /// <summary>
    /// Best-effort mapping from a BCP-47 locale (e.g. <c>en-US</c>, <c>de-DE</c>) to a zone,
    /// for users whose browser has not reported one yet: the region's usual zone, else the
    /// host's zone, else UTC. Never null.
    /// </summary>
    internal static TimeZoneInfo ResolveUserZone(string locale)
    {
        string? region = TryGetRegion(locale);
        string[]? candidates = region is null ? null : LocaleToZones.TryGetValue(region, out string[]? v) ? v : null;
        if (candidates is not null)
        {
            foreach (string id in candidates)
            {
                if (TryFindZone(id) is { } zone)
                    return zone;
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
}
