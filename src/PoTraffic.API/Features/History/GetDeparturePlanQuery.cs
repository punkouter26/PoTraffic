using PoTraffic.API.Infrastructure.Storage;
using PoTraffic.API.Infrastructure.Time;
using PoTraffic.Shared.DTOs.History;

namespace PoTraffic.API.Features.History;

public sealed record GetDeparturePlanQuery(RouteId RouteId, UserId UserId, string DayOfWeek)
    : IRequest<DeparturePlanDto?>;

/// <summary>
/// Turns a route's arrive-by target into a departure time the user can trust.
///
/// <para>
/// The "best time to leave" card picks the slot with the lowest <em>average</em>, but this
/// app exists because commutes are volatile, and the average is exactly the number that
/// makes you late one day in two. The plan instead takes each 5-minute departure slot's
/// 90th-percentile trip and recommends the latest slot where even that trip arrives in time
/// — "leave by 7:40 and you're on time 9 days out of 10".
/// </para>
/// </summary>
public sealed class GetDeparturePlanQueryHandler(TableStorageContext db)
    : IRequestHandler<GetDeparturePlanQuery, DeparturePlanDto?>
{
    /// <summary>Departures considered: this far back from the target arrival.</summary>
    internal const int SearchWindowMinutes = 180;
    private const int SlotMinutes = 5;

    public Task<DeparturePlanDto?> Handle(GetDeparturePlanQuery query, CancellationToken ct)
    {
        EntityRoute? route = db.GetOwnedRoute(query.RouteId, query.UserId, excludeDeleted: true);
        if (route?.ArriveBy is not { } arriveBy)
            return Task.FromResult<DeparturePlanDto?>(null);

        TimeZoneInfo zone = db.ZoneFor(query.UserId);
        List<(DateTimeOffset Local, int Seconds)> all = db.UsualPolls(route.Id)
            .Select(p => (p.PolledAt.ToLocal(zone), p.TravelDurationSeconds))
            .ToList();

        bool daySpecific = Enum.TryParse(query.DayOfWeek, ignoreCase: true, out DayOfWeek dow);
        List<(DateTimeOffset Local, int Seconds)> day = daySpecific
            ? all.Where(p => p.Local.DayOfWeek == dow).ToList()
            : all;
        bool fellBack = day.Count < QuotaConstants.BaselineMinSessionCount;

        Plan? plan = Build(
            (fellBack ? all : day).Select(p => (p.Local.Hour * 60 + p.Local.Minute, p.Seconds)),
            arriveBy.Hour * 60 + arriveBy.Minute);

        string arriveText = arriveBy.ToString("HH:mm");
        return Task.FromResult<DeparturePlanDto?>(plan is null
            ? new DeparturePlanDto(arriveText, null, null, null, 0, 0, !fellBack)
            : new DeparturePlanDto(
                arriveText,
                $"{plan.LeaveByMinute / 60:D2}:{plan.LeaveByMinute % 60:D2}",
                plan.MedianSeconds,
                plan.P90Seconds,
                plan.OnTimePercent,
                plan.SampleCount,
                !fellBack));
    }

    internal sealed record Plan(int LeaveByMinute, int MedianSeconds, int P90Seconds, int OnTimePercent, int SampleCount);

    /// <summary>
    /// The latest slot, within <see cref="SearchWindowMinutes"/> before
    /// <paramref name="arriveByMinute"/>, whose 90th-percentile trip arrives in time. Leaving
    /// is assumed at the slot's start, so every sample in the slot left no earlier than that.
    /// </summary>
    /// <param name="samples">(local minute of day the trip started, trip seconds)</param>
    internal static Plan? Build(IEnumerable<(int Minute, int Seconds)> samples, int arriveByMinute)
    {
        int earliest = Math.Max(0, arriveByMinute - SearchWindowMinutes);
        return samples
            .Where(s => s.Minute >= earliest && s.Minute < arriveByMinute)
            .GroupBy(s => s.Minute / SlotMinutes * SlotMinutes)
            .Where(g => g.Count() >= QuotaConstants.BaselineMinSessionCount)
            .OrderByDescending(g => g.Key)
            .Select(g =>
            {
                List<int> sorted = [.. g.Select(s => s.Seconds).Order()];
                int p90 = sorted[(int)Math.Ceiling(0.9 * sorted.Count) - 1];
                int median = sorted[sorted.Count / 2];
                int onTime = g.Count(s => s.Minute * 60 + s.Seconds <= arriveByMinute * 60);
                return new Plan(g.Key, median, p90, (int)Math.Round(100.0 * onTime / sorted.Count), sorted.Count);
            })
            .FirstOrDefault(p => p.LeaveByMinute * 60 + p.P90Seconds <= arriveByMinute * 60);
    }
}
