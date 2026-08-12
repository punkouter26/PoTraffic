using PoTraffic.API.Infrastructure.Storage;
using PoTraffic.Shared.DTOs.History;

namespace PoTraffic.API.Features.History;

public sealed record GetVolatilityHeatmapQuery(RouteId RouteId, UserId UserId)
    : IRequest<VolatilityHeatmapDto>;

/// <summary>
/// Aggregates a route's whole history into a day-of-week × hour grid (#5), so "Tuesday at
/// 17:00 is the worst hour of your week" is one glance rather than seven baseline reads.
///
/// <para>
/// The reference point is the <em>median</em> sample, not the mean: a handful of 3× outlier
/// commutes drags a mean upward far enough that genuinely congested cells stop looking
/// congested relative to it. Day-of-week and hour are UTC, matching the polling model.
/// </para>
/// </summary>
public sealed class GetVolatilityHeatmapQueryHandler(TableStorageContext db)
    : IRequestHandler<GetVolatilityHeatmapQuery, VolatilityHeatmapDto>
{
    public Task<VolatilityHeatmapDto> Handle(GetVolatilityHeatmapQuery query, CancellationToken ct)
    {
        if (!db.OwnsRoute(query.RouteId, query.UserId))
            return Task.FromResult(new VolatilityHeatmapDto(query.RouteId, 0, 0, []));

        List<PollRecord> polls = db.Polls
            .Where(p => p.RouteId == query.RouteId && !p.IsDeleted)
            .ToList();

        if (polls.Count == 0)
            return Task.FromResult(new VolatilityHeatmapDto(query.RouteId, 0, 0, []));

        List<HeatmapCellDto> cells = [.. polls
            .GroupBy(p => (p.PolledAt.DayOfWeek, p.PolledAt.Hour))
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
                    mean,
                    stdDev,
                    durations.Count);
            })
            .OrderBy(c => c.Hour)];

        return Task.FromResult(new VolatilityHeatmapDto(
            query.RouteId,
            Median([.. polls.Select(p => (double)p.TravelDurationSeconds)]),
            polls.Count,
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
