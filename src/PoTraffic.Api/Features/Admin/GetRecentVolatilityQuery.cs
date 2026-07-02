using PoTraffic.Api.Infrastructure.Storage;

using Microsoft.Extensions.Logging;


using PoTraffic.Shared.DTOs.Admin;
using PoTraffic.Shared.Enums;

namespace PoTraffic.Api.Features.Admin;

// FR-024: Recent volatility — last 7 days × 5-min bucket aggregation.
public sealed record GetRecentVolatilityQuery(int Hours) : IRequest<IReadOnlyList<GlobalVolatilitySlotDto>>;

public sealed class GetRecentVolatilityHandler : IRequestHandler<GetRecentVolatilityQuery, IReadOnlyList<GlobalVolatilitySlotDto>>
{
    private readonly TableStorageContext _db;
    private readonly ILogger<GetRecentVolatilityHandler> _logger;

    public GetRecentVolatilityHandler(TableStorageContext db, ILogger<GetRecentVolatilityHandler> logger)
    {
        _db = db;
        _logger = logger;
    }

    public Task<IReadOnlyList<GlobalVolatilitySlotDto>> Handle(GetRecentVolatilityQuery query, CancellationToken ct)
    {
        DateTimeOffset since = DateTimeOffset.UtcNow.AddDays(-7);

        var polls = _db.Polls.Where(p => p.PolledAt >= since && !p.IsDeleted).ToList();
        var routesById = _db.Routes.ToDictionary(r => r.Id);

        var slots = polls
            .Where(p => routesById.ContainsKey(p.RouteId))
            .GroupBy(p => new
            {
                DayOfWeek = p.PolledAt.DayOfWeek.ToString(),
                TimeSlotBucket = p.PolledAt.Hour * 60 + (p.PolledAt.Minute / 5 * 5),
                ProviderInt = routesById[p.RouteId].Provider
            })
            .Select(g =>
            {
                double mean = g.Average(p => (double)p.TravelDurationSeconds);
                double stddev = g.Count() > 1
                    ? Math.Sqrt(g.Average(p => Math.Pow((double)p.TravelDurationSeconds - mean, 2)))
                    : 0;
                return new GlobalVolatilitySlotDto(
                    DayOfWeek: g.Key.DayOfWeek,
                    TimeSlotBucket: g.Key.TimeSlotBucket,
                    MeanDurationSeconds: Math.Round(mean, 1),
                    StdDevDurationSeconds: Math.Round(stddev, 1),
                    RouteCount: g.Select(p => p.RouteId).Distinct().Count(),
                    Provider: (RouteProvider)g.Key.ProviderInt);
            })
            .OrderBy(s => s.DayOfWeek)
            .ThenBy(s => s.TimeSlotBucket)
            .ToList();

        return Task.FromResult<IReadOnlyList<GlobalVolatilitySlotDto>>(slots);
    }
}
