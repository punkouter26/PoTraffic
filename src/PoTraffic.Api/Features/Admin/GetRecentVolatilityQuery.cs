using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PoTraffic.Api.Infrastructure.Data;
using PoTraffic.Shared.DTOs.Admin;

namespace PoTraffic.Api.Features.Admin;

/// <summary>
/// Returns per-5-minute aggregated travel-time data across all active routes
/// for the past <paramref name="Hours"/> hours (default 24).
/// Used to power the "Past 24h" global volatility chart.
/// </summary>
public sealed record GetRecentVolatilityQuery(int Hours = 24) : IRequest<IReadOnlyList<RecentVolatilityPointDto>>;

public sealed class GetRecentVolatilityHandler
    : IRequestHandler<GetRecentVolatilityQuery, IReadOnlyList<RecentVolatilityPointDto>>
{
    private readonly PoTrafficDbContext _db;
    private readonly ILogger<GetRecentVolatilityHandler> _logger;

    public GetRecentVolatilityHandler(PoTrafficDbContext db, ILogger<GetRecentVolatilityHandler> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<IReadOnlyList<RecentVolatilityPointDto>> Handle(
        GetRecentVolatilityQuery query, CancellationToken ct)
    {
        DateTime sinceUtc = DateTime.UtcNow.AddHours(-query.Hours);

        // Truncate to 5-minute buckets in SQL so grouping is cheap
        const string sql = """
            SELECT
                DATEADD(minute, DATEDIFF(minute, 0, pr.PolledAt) / 5 * 5, 0) AS PolledAt,
                AVG(CAST(pr.TravelDurationSeconds AS float))                   AS MeanDurationSeconds,
                COUNT(DISTINCT pr.RouteId)                                     AS RouteCount
            FROM dbo.PollRecords pr
            WHERE pr.IsDeleted = 0
              AND pr.PolledAt >= {0}
            GROUP BY DATEADD(minute, DATEDIFF(minute, 0, pr.PolledAt) / 5 * 5, 0)
            ORDER BY PolledAt
            """;

        try
        {
            List<RecentVolatilityPointDto> rows = await _db.Database
                .SqlQueryRaw<RecentVolatilityPointDto>(sql, sinceUtc)
                .ToListAsync(ct);
            return rows;
        }
        catch (InvalidOperationException)
        {
            // InMemory provider does not support SqlQueryRaw — fall back to LINQ
            _logger.LogDebug("GetRecentVolatilityQuery: SQL not supported, using LINQ fallback");
            return await FallbackLinqAsync(sinceUtc, ct);
        }
    }

    private async Task<IReadOnlyList<RecentVolatilityPointDto>> FallbackLinqAsync(
        DateTime sinceUtc, CancellationToken ct)
    {
        List<PollRecord> records = await _db.PollRecords
            .Where(pr => !pr.IsDeleted && pr.PolledAt >= sinceUtc)
            .ToListAsync(ct);

        return records
            .GroupBy(pr =>
            {
                long ticks = pr.PolledAt.Ticks;
                long fiveMinTicks = TimeSpan.TicksPerMinute * 5;
                return new DateTime(ticks / fiveMinTicks * fiveMinTicks, DateTimeKind.Utc);
            })
            .OrderBy(g => g.Key)
            .Select(g => new RecentVolatilityPointDto(
                PolledAt: g.Key,
                MeanDurationSeconds: Math.Round(g.Average(pr => (double)pr.TravelDurationSeconds), 1),
                RouteCount: g.Select(pr => pr.RouteId).Distinct().Count()))
            .ToList();
    }
}
