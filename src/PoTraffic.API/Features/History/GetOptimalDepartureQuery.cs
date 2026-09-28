using PoTraffic.API.Infrastructure.Storage;
using PoTraffic.API.Infrastructure.Time;

using Microsoft.Extensions.Logging;

using PoTraffic.API.Features.Routes;
using PoTraffic.Shared.Constants;
using PoTraffic.Shared.DTOs.History;

namespace PoTraffic.API.Features.History;

public sealed record GetOptimalDepartureQuery(
    RouteId RouteId,
    UserId UserId,
    string DayOfWeek);

public sealed class GetOptimalDepartureQueryHandler
{
    private readonly TableStorageContext _db;
    private readonly ILogger<GetOptimalDepartureQueryHandler> _logger;

    public GetOptimalDepartureQueryHandler(
        TableStorageContext db,
        ILogger<GetOptimalDepartureQueryHandler> logger)
    {
        _db = db;
        _logger = logger;
    }

    public Task<OptimalDepartureDto?> Handle(GetOptimalDepartureQuery query, CancellationToken ct)
    {
        // Ownership guard — prevents IDOR
        if (!_db.OwnsRoute(query.RouteId, query.UserId))
            return Task.FromResult<OptimalDepartureDto?>(null);

        // Weekday and time of day are the user's local ones (see UserTime).
        TimeZoneInfo zone = _db.ZoneFor(query.UserId);

        // Day-of-week specific (#4), with an all-days fallback when the requested weekday
        // is still sparse so the "best time to leave" card isn't blank on new routes.
        (List<(DateTimeOffset Local, int Seconds)> source, bool fellBack) =
            _db.LocalPollsForDay(query.RouteId, zone, query.DayOfWeek);

        // Group polls by 5-minute bucket (post-refactor; was SQL STDEV in EF era).
        var buckets = source
            .GroupBy(p => (p.Local.Hour * 60) + (p.Local.Minute / 5 * 5))
            .Select(g => new
            {
                Bucket = g.Key,
                MeanDurationSeconds = g.Average(p => (double)p.Seconds)
            })
            .OrderBy(x => x.Bucket)
            .ToList();

        if (buckets.Count == 0)
            return Task.FromResult<OptimalDepartureDto?>(null);

        double minMean = buckets.Min(b => b.MeanDurationSeconds);
        var optimal = buckets
            .Where(b => b.MeanDurationSeconds <= minMean * 1.05)
            .OrderBy(b => b.Bucket)
            .ToList();

        int startBucket = optimal.First().Bucket;

        return Task.FromResult<OptimalDepartureDto?>(new OptimalDepartureDto(
            query.DayOfWeek,
            startBucket,
            minMean,
            minMean * 0.95,
            minMean * 1.05,
            SampleCount: source.Count,
            DayOfWeekSpecific: !fellBack));
    }
}
