using MediatR;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PoTraffic.Api.Infrastructure.Data;
using PoTraffic.Api.Infrastructure.Data.Projections;
using PoTraffic.Shared.DTOs.Admin;
using PoTraffic.Shared.Enums;

namespace PoTraffic.Api.Features.Admin;

// Query pattern — global volatility aggregation across all routes/users
// FR-024: Groups PollRecords by DayOfWeek × 5-minute TimeSlotBucket across ALL active routes
public sealed record GetGlobalVolatilityQuery : IRequest<IReadOnlyList<GlobalVolatilitySlotDto>>;

public sealed class GetGlobalVolatilityHandler : IRequestHandler<GetGlobalVolatilityQuery, IReadOnlyList<GlobalVolatilitySlotDto>>
{
    private readonly PoTrafficDbContext _db;
    private readonly ILogger<GetGlobalVolatilityHandler> _logger;

    public GetGlobalVolatilityHandler(PoTrafficDbContext db, ILogger<GetGlobalVolatilityHandler> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<IReadOnlyList<GlobalVolatilitySlotDto>> Handle(GetGlobalVolatilityQuery query, CancellationToken ct)
    {
        // Raw SQL aggregation — avoids loading all PollRecords into memory (O(N) → O(groups))
        const string sql = """
            SELECT
                DATENAME(dw, pr.PolledAt)                                            AS DayOfWeek,
                (DATEPART(hh, pr.PolledAt) * 60) + (DATEPART(mi, pr.PolledAt) / 5 * 5) AS TimeSlotBucket,
                r.Provider                                                           AS ProviderInt,
                AVG(CAST(pr.TravelDurationSeconds AS float))                         AS MeanDurationSeconds,
                STDEV(CAST(pr.TravelDurationSeconds AS float))                       AS StdDevDurationSeconds,
                COUNT(DISTINCT pr.RouteId)                                           AS RouteCount
            FROM dbo.PollRecords pr
            INNER JOIN dbo.Routes r ON r.Id = pr.RouteId
            WHERE pr.IsDeleted = 0
            GROUP BY
                DATENAME(dw, pr.PolledAt),
                (DATEPART(hh, pr.PolledAt) * 60) + (DATEPART(mi, pr.PolledAt) / 5 * 5),
                r.Provider
            ORDER BY DayOfWeek, TimeSlotBucket
            """;

        List<GlobalVolatilityProjection> rows;

        try
        {
            rows = await _db.Database
                .SqlQueryRaw<GlobalVolatilityProjection>(sql)
                .ToListAsync(ct);
        }
        catch (InvalidOperationException)
        {
            // InMemory provider does not support SqlQueryRaw — fall back to LINQ for test environments
            _logger.LogDebug("GetGlobalVolatilityQuery: SQL not supported on InMemory provider, using LINQ fallback");
            return await FallbackLinqAsync(ct);
        }

        return rows
            .Select(r => new GlobalVolatilitySlotDto(
                DayOfWeek: r.DayOfWeek,
                TimeSlotBucket: r.TimeSlotBucket,
                MeanDurationSeconds: Math.Round(r.MeanDurationSeconds, 1),
                StdDevDurationSeconds: r.StdDevDurationSeconds.HasValue
                    ? Math.Round(r.StdDevDurationSeconds.Value, 1)
                    : null,
                RouteCount: r.RouteCount,
                Provider: (RouteProvider)r.ProviderInt))
            .ToList();
    }

    // LINQ fallback — used by InMemory database in test environments only
    private async Task<IReadOnlyList<GlobalVolatilitySlotDto>> FallbackLinqAsync(CancellationToken ct)
    {
        List<PollRecord> records = await _db.PollRecords
            .Include(pr => pr.Route)
            .Where(pr => !pr.IsDeleted)
            .ToListAsync(ct);

        return records
            .GroupBy(pr => new
            {
                DayOfWeek = pr.PolledAt.DayOfWeek.ToString(),
                TimeSlotBucket = pr.PolledAt.Hour * 60 + (pr.PolledAt.Minute / 5 * 5),
                ProviderInt = (int)pr.Route!.Provider
            })
            .Select(g =>
            {
                double mean = g.Average(pr => (double)pr.TravelDurationSeconds);
                double? stddev = g.Count() > 1
                    ? Math.Sqrt(g.Average(pr => Math.Pow((double)pr.TravelDurationSeconds - mean, 2)))
                    : (double?)null;
                return new GlobalVolatilitySlotDto(
                    DayOfWeek: g.Key.DayOfWeek,
                    TimeSlotBucket: g.Key.TimeSlotBucket,
                    MeanDurationSeconds: Math.Round(mean, 1),
                    StdDevDurationSeconds: stddev.HasValue ? Math.Round(stddev.Value, 1) : null,
                    RouteCount: g.Select(pr => pr.RouteId).Distinct().Count(),
                    Provider: (RouteProvider)g.Key.ProviderInt);
            })
            .OrderBy(s => s.DayOfWeek)
            .ThenBy(s => s.TimeSlotBucket)
            .ToList();
    }
}
