using PoTraffic.Shared.DTOs.Admin;
using PoTraffic.Shared.Enums;

namespace PoTraffic.API.Features.Admin;

/// <summary>
/// Volatility aggregation shared by the global (all-time) and recent (last 7 days) admin
/// queries — they differ only in which polls they feed in, so the bucketing rule and the
/// statistics live here once.
/// </summary>
internal static class VolatilityAggregator
{
    /// <summary>
    /// Groups <paramref name="polls"/> by day-of-week × 5-minute bucket × provider and
    /// returns each slot's mean, population standard deviation, and distinct route count.
    /// Each group is walked once — mean, σ and the route set all come from the same pass.
    /// </summary>
    public static IReadOnlyList<GlobalVolatilitySlotDto> Aggregate(
        IEnumerable<PollRecord> polls,
        IReadOnlyDictionary<RouteId, EntityRoute> routesById)
    {
        return [.. polls
            .Where(p => routesById.ContainsKey(p.RouteId))
            .GroupBy(p => new
            {
                DayOfWeek = p.PolledAt.DayOfWeek.ToString(),
                TimeSlotBucket = p.PolledAt.Hour * 60 + (p.PolledAt.Minute / 5 * 5),
                ProviderInt = routesById[p.RouteId].Provider
            })
            .Select(g =>
            {
                int count = 0;
                double sum = 0, sumOfSquares = 0;
                var routes = new HashSet<RouteId>();

                foreach (PollRecord p in g)
                {
                    double seconds = p.TravelDurationSeconds;
                    count++;
                    sum += seconds;
                    sumOfSquares += seconds * seconds;
                    routes.Add(p.RouteId);
                }

                double mean = sum / count;
                // Population variance; clamped because floating-point cancellation can push
                // a genuinely-zero variance a hair below zero.
                double variance = count > 1 ? Math.Max(0, sumOfSquares / count - mean * mean) : 0;

                return new GlobalVolatilitySlotDto(
                    DayOfWeek: g.Key.DayOfWeek,
                    TimeSlotBucket: g.Key.TimeSlotBucket,
                    MeanDurationSeconds: Math.Round(mean, 1),
                    StdDevDurationSeconds: Math.Round(Math.Sqrt(variance), 1),
                    RouteCount: routes.Count,
                    Provider: (RouteProvider)g.Key.ProviderInt);
            })
            .OrderBy(s => s.DayOfWeek)
            .ThenBy(s => s.TimeSlotBucket)];
    }
}
