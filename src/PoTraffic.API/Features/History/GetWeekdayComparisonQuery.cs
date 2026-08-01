using PoTraffic.API.Infrastructure.Storage;
using PoTraffic.Shared.DTOs.History;

namespace PoTraffic.API.Features.History;

public sealed record GetWeekdayComparisonQuery(RouteId RouteId, UserId UserId)
    : IRequest<WeekdayComparisonDto>;

/// <summary>
/// Mean travel time per day-of-week across a route's whole history (#4), so the UI can
/// surface "Fridays run 20% worse than your best day". Day-of-week is UTC, matching the
/// polling/window model.
/// </summary>
public sealed class GetWeekdayComparisonQueryHandler(TableStorageContext db)
    : IRequestHandler<GetWeekdayComparisonQuery, WeekdayComparisonDto>
{
    private static readonly DayOfWeek[] Week =
        [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday,
         DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday];

    public Task<WeekdayComparisonDto> Handle(GetWeekdayComparisonQuery query, CancellationToken ct)
    {
        bool owned = db.Routes.Any(r => r.Id == query.RouteId && r.UserId == query.UserId);
        if (!owned)
            return Task.FromResult(new WeekdayComparisonDto(query.RouteId, [], null, null));

        var byDay = db.Polls
            .Where(p => p.RouteId == query.RouteId && !p.IsDeleted)
            .GroupBy(p => p.PolledAt.DayOfWeek)
            .ToDictionary(g => g.Key, g => (Mean: g.Average(p => (double)p.TravelDurationSeconds), Count: g.Count()));

        List<WeekdayMeanDto> days = [.. Week.Select(d =>
        {
            byDay.TryGetValue(d, out (double Mean, int Count) stats);
            return new WeekdayMeanDto(d.ToString(), stats.Mean, stats.Count);
        })];

        var sampled = days.Where(d => d.SampleCount > 0).ToList();
        string? best = sampled.Count > 0 ? sampled.MinBy(d => d.MeanDurationSeconds)!.DayOfWeek : null;
        string? worst = sampled.Count > 0 ? sampled.MaxBy(d => d.MeanDurationSeconds)!.DayOfWeek : null;

        return Task.FromResult(new WeekdayComparisonDto(query.RouteId, days, best, worst));
    }
}
