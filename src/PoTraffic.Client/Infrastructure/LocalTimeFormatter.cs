// filepath: src/PoTraffic.Client/Infrastructure/LocalTimeFormatter.cs
using System.Globalization;

namespace PoTraffic.Client.Infrastructure;

/// <summary>
/// Formats window times for display.
///
/// Windows created before zones were captured store <c>TimeOnly</c> values in UTC (see
/// PollRouteJob.IsWithinWindow, MonitoringWindow.ToDto) and the wire DTOs carry them as
/// opaque <c>"HH:mm"</c> strings; newer windows carry their zone and wall-clock times. Rendering those verbatim would show e.g. "13:40 – 01:40" to an Eastern
/// user who picked 9:40 AM – 9:40 PM. Every display layer should route through this
/// helper so the round-trip stays consistent and unit-testable.
/// </summary>
public static class LocalTimeFormatter
{
    /// <summary>
    /// Format a UTC "HH:mm" string as the user's local-time "h:mm tt" (e.g. "9:40 AM").
    /// Empty / unparseable input is returned unchanged.
    /// </summary>
    /// <param name="utcHHmm">Wire-format UTC time, e.g. "13:40".</param>
    /// <param name="nowUtc">Override for the current UTC instant (used by tests to fix the day).</param>
    /// <param name="localZone">Override for the target time zone (used by tests).</param>
    public static string FormatUtcHHmmAsLocal(
        string utcHHmm,
        DateTime? nowUtc = null,
        TimeZoneInfo? localZone = null)
    {
        if (string.IsNullOrWhiteSpace(utcHHmm)) return utcHHmm;
        if (!TimeOnly.TryParse(utcHHmm, out TimeOnly t)) return utcHHmm;

        DateTime utcToday = (nowUtc ?? DateTime.UtcNow).Date;
        DateTime utc = utcToday.Add(t.ToTimeSpan());
        DateTime local = TimeZoneInfo.ConvertTimeFromUtc(utc, localZone ?? TimeZoneInfo.Local);
        return local.ToString("h:mm tt", CultureInfo.CurrentCulture);
    }

    /// <summary>
    /// Format a wall-clock "HH:mm" (a window stored with its zone) as "h:mm tt" with no
    /// conversion. Empty / unparseable input is returned unchanged.
    /// </summary>
    public static string FormatWallClock(string hhmm) =>
        TimeOnly.TryParse(hhmm, CultureInfo.InvariantCulture, out TimeOnly t)
            ? t.ToString("h:mm tt", CultureInfo.CurrentCulture)
            : hhmm;
}