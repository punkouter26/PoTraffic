using MediatR;
using PoTraffic.Api.Infrastructure.Storage;

using Microsoft.Extensions.Logging;


using PoTraffic.Shared.DTOs.History;

namespace PoTraffic.Api.Features.History;

public sealed record GetBaselineQuery(
    Guid RouteId,
    Guid UserId,
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
        // Post-refactor: SQL Server STDEV() aggregation is replaced with a pure-LINQ
        // pass over PollRecords. For small-to-medium route histories (the common case)
        // the in-process LINQ query is fine; for very large histories (10k+ polls) a
        // future Table Storage stored-procedure would be a follow-up optimisation.
        bool owned = _db.Routes
            .Any(r => r.Id == query.RouteId && r.UserId == query.UserId);
        if (!owned)
            return Task.FromResult(new BaselineResponse(query.RouteId, query.DayOfWeek, 0, []));

        // Group polls by 15-minute bucket; compute mean + sample stddev.
        var buckets = _db.Polls
            .Where(p => p.RouteId == query.RouteId && !p.IsDeleted)
            .GroupBy(p => (p.PolledAt.Hour * 4) + (p.PolledAt.Minute / 15))
            .ToList();

        var slots = buckets
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
            slots));
    }
}
