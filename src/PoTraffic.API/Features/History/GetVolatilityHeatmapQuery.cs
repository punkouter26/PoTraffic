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
/// congested relative to it. Day-of-week and half-hour are bucketed in US Eastern time,
/// so a 17:30 row reads as the rush hour the user actually drives in.
/// </para>
/// </summary>
public sealed class GetVolatilityHeatmapQueryHandler(TableStorageContext db)
    : IRequestHandler<GetVolatilityHeatmapQuery, VolatilityHeatmapDto>
{
    /// <summary>US Eastern (handles EST/EDT automatically). Resolved once per process.</summary>
    private static readonly TimeZoneInfo Eastern = ResolveEastern();

    public Task<VolatilityHeatmapDto> Handle(GetVolatilityHeatmapQuery query, CancellationToken ct)
    {
        if (!db.OwnsRoute(query.RouteId, query.UserId))
            return Task.FromResult(new VolatilityHeatmapDto(query.RouteId, 0, 0, []));

        List<PollRecord> polls = db.Polls
            .Where(p => p.RouteId == query.RouteId)
            .ToList();

        if (polls.Count == 0)
            return Task.FromResult(new VolatilityHeatmapDto(query.RouteId, 0, 0, []));

        List<HeatmapCellDto> cells = [.. polls
            .GroupBy(p =>
            {
                DateTimeOffset local = TimeZoneInfo.ConvertTime(p.PolledAt, Eastern);
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
            cells));
    }

    /// <summary>
    /// Resolves the US Eastern time zone, falling back to a fixed UTC-5 offset if the host
    /// has no IANA/Windows mapping (very rare). The fixed offset ignores DST, so prefer
    /// the resolved zone whenever it is available.
    /// </summary>
    private static TimeZoneInfo ResolveEastern()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.CreateCustomTimeZone("EST", TimeSpan.FromHours(-5), "Eastern Standard Time", "EST");
        }
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
