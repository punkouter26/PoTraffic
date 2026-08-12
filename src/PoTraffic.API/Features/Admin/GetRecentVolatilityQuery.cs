using PoTraffic.API.Infrastructure.Storage;

using Microsoft.Extensions.Logging;


using PoTraffic.Shared.DTOs.Admin;
using PoTraffic.Shared.Enums;

namespace PoTraffic.API.Features.Admin;

/// <summary>
/// Last-24h aggregated volatility for the admin chart. The wire shape is a real
/// timestamped series (<see cref="RecentVolatilityPointDto"/>), bucketed every five
/// minutes from now backwards. Used to drive the time-series chart on the Admin tab.
/// </summary>
public sealed record GetRecentVolatilityQuery(int Hours) : IRequest<IReadOnlyList<RecentVolatilityPointDto>>;

public sealed class GetRecentVolatilityHandler : IRequestHandler<GetRecentVolatilityQuery, IReadOnlyList<RecentVolatilityPointDto>>
{
    private readonly TableStorageContext _db;
    private readonly ILogger<GetRecentVolatilityHandler> _logger;

    public GetRecentVolatilityHandler(TableStorageContext db, ILogger<GetRecentVolatilityHandler> logger)
    {
        _db = db;
        _logger = logger;
    }

    public Task<IReadOnlyList<RecentVolatilityPointDto>> Handle(GetRecentVolatilityQuery query, CancellationToken ct)
    {
        DateTimeOffset since = DateTimeOffset.UtcNow.AddHours(-query.Hours);

        List<PollRecord> polls = _db.Polls
            .Where(p => p.PolledAt >= since)
            .ToList();

        // Bucket by UTC wall-clock minute truncated to 5 — every chart in the app plots
        // per-bucket mean so the line is the average across all active routes.
        var byBucket = polls
            .GroupBy(p => new DateTime(
                p.PolledAt.UtcDateTime.Year,
                p.PolledAt.UtcDateTime.Month,
                p.PolledAt.UtcDateTime.Day,
                p.PolledAt.UtcDateTime.Hour,
                (p.PolledAt.UtcDateTime.Minute / 5) * 5,
                0,
                DateTimeKind.Utc))
            .OrderBy(g => g.Key)
            .Select(g => new RecentVolatilityPointDto(
                PolledAt: g.Key,
                MeanDurationSeconds: g.Average(p => (double)p.TravelDurationSeconds),
                RouteCount: g.Select(p => p.RouteId).Distinct().Count()))
            .ToList();

        return Task.FromResult<IReadOnlyList<RecentVolatilityPointDto>>(byBucket);
    }
}