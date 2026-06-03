using MediatR;
using PoTraffic.Api.Infrastructure.Storage;

using Microsoft.Extensions.Logging;


using PoTraffic.Shared.DTOs.History;

namespace PoTraffic.Api.Features.History;

public sealed record GetOptimalDepartureQuery(
    Guid RouteId,
    Guid UserId,
    string DayOfWeek) : IRequest<OptimalDepartureDto?>;

public sealed class GetOptimalDepartureQueryHandler
    : IRequestHandler<GetOptimalDepartureQuery, OptimalDepartureDto?>
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
        bool owned = _db.Routes
            .Any(r => r.Id == query.RouteId && r.UserId == query.UserId);
        if (!owned)
            return Task.FromResult<OptimalDepartureDto?>(null);

        // Group polls by 5-minute bucket (post-refactor; was SQL STDEV in EF era).
        var buckets = _db.Polls
            .Where(p => p.RouteId == query.RouteId && !p.IsDeleted)
            .GroupBy(p => (p.PolledAt.Hour * 60) + (p.PolledAt.Minute / 5) * 5)
            .Select(g => new
            {
                Bucket = g.Key,
                MeanDurationSeconds = g.Average(p => (double)p.TravelDurationSeconds)
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
        int endBucket = optimal.Last().Bucket;

        return Task.FromResult<OptimalDepartureDto?>(new OptimalDepartureDto(
            query.DayOfWeek,
            startBucket,
            minMean,
            minMean * 0.95,
            minMean * 1.05));
    }
}
