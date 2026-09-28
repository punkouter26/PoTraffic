using PoTraffic.Shared.DTOs.History;

namespace PoTraffic.API.Features.History;

/// <summary>
/// Builds an RFC 5545 VCALENDAR (.ics) with a weekly-recurring "leave by" reminder at a
/// route's optimal departure slot (#2), so users can add the recommendation to any calendar.
/// The slot is the user's local time, written as an RFC 5545 "floating" time (no Z, no TZID):
/// the calendar keeps it at that wall-clock time in its own zone, so DST never moves it.
/// </summary>
internal static class DepartureCalendar
{
    // Indexed by (int)DayOfWeek: Sunday = 0 … Saturday = 6.
    private static readonly string[] IcsDays = ["SU", "MO", "TU", "WE", "TH", "FR", "SA"];

    public static string Build(RouteId routeId, string destination, OptimalDepartureDto opt, TimeZoneInfo zone)
    {
        DayOfWeek dow = Enum.TryParse(opt.DayOfWeek, ignoreCase: true, out DayOfWeek d) ? d : DayOfWeek.Monday;
        int hour = opt.TimeSlotBucket / 60;
        int minute = opt.TimeSlotBucket % 60;

        DateTime nowUtc = DateTime.UtcNow;
        DateTime start = NextOccurrence(TimeZoneInfo.ConvertTimeFromUtc(nowUtc, zone), dow, hour, minute);
        int mins = (int)Math.Round(opt.PredictedDurationSeconds / 60);

        string[] lines =
        [
            "BEGIN:VCALENDAR",
            "VERSION:2.0",
            "PRODID:-//PoTraffic//Departure//EN",
            "CALSCALE:GREGORIAN",
            "METHOD:PUBLISH",
            "BEGIN:VEVENT",
            $"UID:{routeId}-{opt.DayOfWeek}@potraffic",
            $"DTSTAMP:{nowUtc:yyyyMMdd'T'HHmmss'Z'}",
            $"DTSTART:{start:yyyyMMdd'T'HHmmss}",
            "DURATION:PT5M",
            $"RRULE:FREQ=WEEKLY;BYDAY={IcsDays[(int)dow]}",
            $"SUMMARY:Leave for {Escape(destination)} (~{mins} min)",
            $"DESCRIPTION:PoTraffic optimal departure for your commute on {opt.DayOfWeek}s.",
            "BEGIN:VALARM",
            "TRIGGER:-PT10M",
            "ACTION:DISPLAY",
            "DESCRIPTION:Time to leave soon",
            "END:VALARM",
            "END:VEVENT",
            "END:VCALENDAR",
        ];
        return string.Join("\r\n", lines) + "\r\n";
    }

    private static DateTime NextOccurrence(DateTime fromLocal, DayOfWeek dow, int hour, int minute)
    {
        for (int i = 0; i <= 7; i++)
        {
            DateTime candidate = fromLocal.Date.AddDays(i).AddHours(hour).AddMinutes(minute);
            if (candidate.DayOfWeek == dow && candidate > fromLocal)
                return candidate;
        }
        return fromLocal.Date.AddHours(hour).AddMinutes(minute);
    }

    private static string Escape(string s) => s
        .Replace("\\", "\\\\").Replace(",", "\\,").Replace(";", "\\;").Replace("\n", "\\n");
}
