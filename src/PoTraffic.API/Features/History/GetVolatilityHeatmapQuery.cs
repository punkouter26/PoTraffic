using PoTraffic.API.Infrastructure.Storage;
using PoTraffic.API.Infrastructure.Time;
using PoTraffic.Shared.DTOs.History;

namespace PoTraffic.API.Features.History;

public sealed record GetVolatilityHeatmapQuery(RouteId RouteId, UserId UserId)
    : IRequest<VolatilityHeatmapDto>;

/// <summary>
/// Aggregates a route's whole history into a day-of-week × quarter-hour grid (#5), so
/// "Tuesday at 17:30 is the worst 15 minutes of your week" is one glance rather than
/// seven baseline reads.
///
/// <para>
/// The reference point is the <em>median</em> sample, not the mean: a handful of 3× outlier
/// commutes drags a mean upward far enough that genuinely congested cells stop looking
/// congested relative to it. Day-of-week and quarter-hour are bucketed in the user's local
/// time zone (<see cref="UserTime.ZoneFor"/>), so a 17:30 row reads as the rush
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

        List<PollRecord> polls = [.. db.UsualPolls(query.RouteId)];

        if (polls.Count == 0)
            return Task.FromResult(new VolatilityHeatmapDto(query.RouteId, 0, 0, TimeZoneInfo.Utc.Id, []));

        TimeZoneInfo userZone = db.ZoneFor(query.UserId);
        string zoneId = userZone.Id;

        List<HeatmapCellDto> cells = [.. polls
            .GroupBy(p =>
            {
                DateTimeOffset local = TimeZoneInfo.ConvertTime(p.PolledAt, userZone);
                return (
                    local.DayOfWeek,
                    local.Hour,
                    // 15-minute resolution: :00 :15 :30 :45. Integer division floors
                    // the minute into its quarter, so 11:44 lands in quarter 2 (:30).
                    Quarter: local.Minute / 15);
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
                    g.Key.Quarter,
                    mean,
                    stdDev,
                    durations.Count);
            })
            .OrderBy(c => c.Hour)
            .ThenBy(c => c.Quarter)];

        return Task.FromResult(new VolatilityHeatmapDto(
            query.RouteId,
            Median([.. polls.Select(p => (double)p.TravelDurationSeconds)]),
            polls.Count,
            zoneId,
            cells));
    }

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
