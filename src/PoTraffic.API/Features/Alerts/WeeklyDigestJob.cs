using System.Globalization;
using Microsoft.Extensions.Logging;
using PoTraffic.API.Infrastructure.Storage;
using PoTraffic.API.Infrastructure.Time;
using PoTraffic.Shared.Enums;

namespace PoTraffic.API.Features.Alerts;

/// <summary>
/// Monday's look back at each commute: the quickest and slowest day, how the week compared
/// with the ones before, and how much the choice of departure time is worth. Stored as a
/// "Digest" alert (so it reaches the bell even without push) and pushed.
///
/// <para>Runs daily at 12:00 UTC and only acts on Mondays — a morning read for the Americas,
/// lunchtime for Europe — because the scheduler's recurrence is daily-at-a-time.</para>
/// </summary>
public sealed class WeeklyDigestJob(TableStorageContext db, IPushNotifier push, ILogger<WeeklyDigestJob> logger)
{
    internal const string DigestKind = "Digest";
    private const int HistoryDays = 35;

    public async Task ExecuteAsync(CancellationToken ct = default)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (now.DayOfWeek != DayOfWeek.Monday)
            return;

        List<(Alert Alert, string Title)> digests = [];
        foreach (EntityRoute route in db.Routes
            .Where(r => !r.IsSample && r.MonitoringStatus != (int)MonitoringStatus.Deleted)
            .ToList())
        {
            TimeZoneInfo zone = db.ZoneFor(route.UserId);
            List<(DateTimeOffset Local, int Seconds)> samples = db.UsualPolls(route.Id)
                .Where(p => p.PolledAt >= now.AddDays(-HistoryDays))
                .Select(p => (p.PolledAt.ToLocal(zone), p.TravelDurationSeconds))
                .ToList();

            if (Summarize(samples, now.ToLocal(zone)) is not { } text)
                continue;

            var alert = new Alert
            {
                Id = AlertId.New(),
                UserId = route.UserId,
                RouteId = route.Id,
                Kind = DigestKind,
                Message = text,
                CreatedAt = now,
            };
            db.Add(alert);
            string label = route.Name ?? route.DestinationAddress.Split(',')[0].Trim();
            digests.Add((alert, $"Your week: {label}"));
        }

        if (digests.Count == 0)
            return;
        await db.SaveChangesAsync(ct);

        foreach ((Alert alert, string title) in digests)
        {
            try
            {
                await push.SendAsync(alert.UserId,
                    new PushPayload(title, alert.Message, $"/routes/{alert.RouteId}", $"digest-{alert.RouteId}"),
                    TimeSpan.FromDays(1), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Digest push failed for route {RouteId}", alert.RouteId);
            }
        }
    }

    /// <summary>
    /// The digest text for one route, or null when last week has too little to say anything.
    /// </summary>
    /// <param name="samples">Usual-day samples, local time, covering the last five weeks.</param>
    internal static string? Summarize(List<(DateTimeOffset Local, int Seconds)> samples, DateTimeOffset nowLocal)
    {
        DateTime weekStart = nowLocal.Date.AddDays(-7);
        List<(DateTimeOffset Local, int Seconds)> week =
            [.. samples.Where(s => s.Local.DateTime >= weekStart && s.Local.DateTime < nowLocal.Date)];
        if (week.Count < QuotaConstants.BaselineMinSessionCount)
            return null;

        List<string> parts = [];
        CultureInfo en = CultureInfo.InvariantCulture;

        var days = week
            .GroupBy(s => s.Local.Date)
            .Select(g => (Day: g.Key, Minutes: Minutes(Median(g.Select(s => s.Seconds)))))
            .OrderBy(d => d.Minutes)
            .ToList();
        if (days.Count >= 2 && days[^1].Minutes > days[0].Minutes)
            parts.Add($"{days[0].Day.ToString("dddd", en)} was quickest ({days[0].Minutes} min), " +
                      $"{days[^1].Day.ToString("dddd", en)} slowest ({days[^1].Minutes} min).");

        List<int> before = [.. samples.Where(s => s.Local.DateTime < weekStart).Select(s => s.Seconds)];
        if (before.Count >= QuotaConstants.BaselineMinSessionCount)
        {
            int delta = Minutes(week.Average(s => s.Seconds) - before.Average());
            parts.Add(delta switch
            {
                > 0 => $"About {delta} min slower than the weeks before.",
                < 0 => $"About {-delta} min quicker than the weeks before.",
                _ => "Right in line with the weeks before.",
            });
        }

        // What the departure time is worth: best vs worst quarter-hour you actually drive in.
        var slots = samples
            .GroupBy(s => UserTime.QuarterOfDay(s.Local))
            .Where(g => g.Count() >= QuotaConstants.BaselineMinSessionCount)
            .Select(g => (Quarter: g.Key, Mean: g.Average(s => s.Seconds)))
            .OrderBy(s => s.Mean)
            .ToList();
        if (slots.Count >= 2 && Minutes(slots[^1].Mean - slots[0].Mean) is var saved and >= 2)
            parts.Add($"Leaving at {Clock(slots[0].Quarter)} instead of {Clock(slots[^1].Quarter)} " +
                      $"saves about {saved} min a trip.");

        return parts.Count == 0 ? null : string.Join(" ", parts);

        static int Minutes(double seconds) => (int)Math.Round(seconds / 60);
        static string Clock(int quarter) => $"{quarter / 4:D2}:{quarter % 4 * 15:D2}";
    }

    private static double Median(IEnumerable<int> values)
    {
        List<int> sorted = [.. values.Order()];
        int mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }
}
