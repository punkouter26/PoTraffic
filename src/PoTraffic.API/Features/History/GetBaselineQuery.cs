using PoTraffic.API.Infrastructure.Storage;

using Microsoft.Extensions.Logging;

using PoTraffic.API.Features.Routes;
using PoTraffic.Shared.Constants;
using PoTraffic.Shared.DTOs.History;

namespace PoTraffic.API.Features.History;

public sealed record GetBaselineQuery(
    RouteId RouteId,
    UserId UserId,
    string DayOfWeek) : IRequest<BaselineResponse>;

public sealed class GetBaselineQueryHandler
    : IRequestHandler<GetBaselineQuery, BaselineResponse>
{
    private readonly TableStorageContext _db;
    private readonly ILogger<GetBaselineQueryHandler> _logger;

    public GetBaselineQueryHandler(TableStorageContext db, ILogger<GetBaselineQueryHandler> logger)
    {
        _db = db;
        _logger = logger;
    }

    public Task<BaselineResponse> Handle(GetBaselineQuery query, CancellationToken ct)
    {
        // For small-to-medium route histories (the common case), the in-process
        // LINQ query is fine; very large histories can move to a precomputed
        // Table Storage aggregate in a follow-up optimisation.
        if (!_db.OwnsRoute(query.RouteId, query.UserId))
            return Task.FromResult(new BaselineResponse(query.RouteId, query.DayOfWeek, 0, [], true));

        List<PollRecord> allPolls = _db.Polls
            .Where(p => p.RouteId == query.RouteId)
            .ToList();

        // Baseline is now day-of-week specific (#4): a Friday baseline reflects only
        // Friday history. Times/day-of-week are UTC, matching the polling model.
        bool daySpecific = Enum.TryParse(query.DayOfWeek, ignoreCase: true, out DayOfWeek dow);
        List<PollRecord> dayPolls = daySpecific
            ? allPolls.Where(p => p.PolledAt.DayOfWeek == dow).ToList()
            : allPolls;

        // Fall back to all days when this weekday hasn't accumulated enough samples yet,
        // so a new route still renders a usable baseline instead of a blank chart.
        bool fellBack = dayPolls.Count < QuotaConstants.BaselineMinSessionCount
            && allPolls.Count > dayPolls.Count;
        List<PollRecord> source = fellBack ? allPolls : dayPolls;

        var slots = source
            .GroupBy(p => (p.PolledAt.Hour * 4) + (p.PolledAt.Minute / 15))
            .Select(g =>
            {
                var durations = g.Select(p => (double)p.TravelDurationSeconds).ToList();
                double mean = durations.Average();
                double stddev = durations.Count > 1
                    ? Math.Sqrt(durations.Sum(d => Math.Pow(d - mean, 2)) / (durations.Count - 1))
                    : 0;
                return new PoTraffic.Shared.DTOs.History.BaselineSlotDto(
                    DayOfWeek: query.DayOfWeek,
                    TimeSlotBucket: g.Key,
                    MeanDurationSeconds: (int)Math.Round(mean),
                    StdDevDurationSeconds: (int)Math.Round(stddev),
                    SessionCount: g.Count());
            })
            .OrderBy(s => s.TimeSlotBucket)
            .ToList();

        return Task.FromResult(new BaselineResponse(
            query.RouteId,
            query.DayOfWeek,
            slots.Sum(s => s.SessionCount),
            slots,
            DayOfWeekSpecific: !fellBack));
    }
}
