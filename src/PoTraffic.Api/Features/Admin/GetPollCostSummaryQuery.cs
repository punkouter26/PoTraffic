using MediatR;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PoTraffic.Api.Infrastructure.Data;

using PoTraffic.Api.Infrastructure.Data.Projections;
using PoTraffic.Shared.DTOs.Admin;
using PoTraffic.Shared.Enums;

namespace PoTraffic.Api.Features.Admin;

// Query pattern — per-provider poll cost summary for current UTC day
public sealed record GetPollCostSummaryQuery : IRequest<IReadOnlyList<PollCostSummaryDto>>;

public sealed class GetPollCostSummaryHandler : IRequestHandler<GetPollCostSummaryQuery, IReadOnlyList<PollCostSummaryDto>>
{
    private readonly PoTrafficDbContext _db;
    private readonly ILogger<GetPollCostSummaryHandler> _logger;

    public GetPollCostSummaryHandler(PoTrafficDbContext db, ILogger<GetPollCostSummaryHandler> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<IReadOnlyList<PollCostSummaryDto>> Handle(GetPollCostSummaryQuery query, CancellationToken ct)
    {
        DateTimeOffset dayStart = DateTimeOffset.UtcNow.Date;
        DateTimeOffset dayEnd = dayStart.AddDays(1);

        // Raw SQL aggregation — avoids loading all of today's PollRecords into memory
        const string sql = """
            SELECT r.Provider AS ProviderInt, COUNT(*) AS PollCount
            FROM dbo.PollRecords pr
            INNER JOIN dbo.Routes r ON r.Id = pr.RouteId
            WHERE pr.PolledAt >= @dayStart AND pr.PolledAt < @dayEnd
              AND pr.IsDeleted = 0
            GROUP BY r.Provider
            """;

        List<PollCostProjection> rows;

        try
        {
            rows = await _db.Database
                .SqlQueryRaw<PollCostProjection>(
                    sql,
                    new SqlParameter("@dayStart", System.Data.SqlDbType.DateTimeOffset) { Value = dayStart },
                    new SqlParameter("@dayEnd", System.Data.SqlDbType.DateTimeOffset) { Value = dayEnd })
                .ToListAsync(ct);
        }
        catch (InvalidOperationException)
        {
            // InMemory provider does not support SqlQueryRaw — fall back to LINQ for test environments
            _logger.LogDebug("GetPollCostSummaryQuery: SQL not supported on InMemory provider, using LINQ fallback");
            return await FallbackLinqAsync(dayStart, dayEnd, ct);
        }

        if (rows.Count == 0) return [];

        // Load cost rates from SystemConfiguration
        List<SystemConfiguration> configs = await _db.SystemConfigurations
            .Where(c => c.Key.StartsWith("cost.perpoll."))
            .ToListAsync(ct);

        double googleCost = GetCost(configs, "cost.perpoll.googlemaps");
        double tomtomCost = GetCost(configs, "cost.perpoll.tomtom");
        DateTimeOffset now = DateTimeOffset.UtcNow;

        return rows
            .Select(r =>
            {
                RouteProvider provider = (RouteProvider)r.ProviderInt;
                double costPerPoll = provider == RouteProvider.TomTom ? tomtomCost : googleCost;
                return new PollCostSummaryDto(
                    AsOfUtc: now,
                    Provider: provider,
                    TotalPollCount: r.PollCount,
                    TotalEstimatedCostUsd: r.PollCount * costPerPoll);
            })
            .ToList();
    }

    private static double GetCost(List<SystemConfiguration> configs, string key)
    {
        string? value = configs.FirstOrDefault(c => c.Key == key)?.Value;
        return double.TryParse(value, out double result) ? result : 0.0;
    }

    // LINQ fallback — used by InMemory database in test environments only
    private async Task<IReadOnlyList<PollCostSummaryDto>> FallbackLinqAsync(
        DateTimeOffset dayStart, DateTimeOffset dayEnd, CancellationToken ct)
    {
        List<PollRecord> records = await _db.PollRecords
            .Include(pr => pr.Route)
            .Where(pr => pr.PolledAt >= dayStart && pr.PolledAt < dayEnd && !pr.IsDeleted)
            .ToListAsync(ct);

        if (records.Count == 0) return [];

        List<SystemConfiguration> configs = await _db.SystemConfigurations
            .Where(c => c.Key.StartsWith("cost.perpoll."))
            .ToListAsync(ct);

        double googleCost = GetCost(configs, "cost.perpoll.googlemaps");
        double tomtomCost = GetCost(configs, "cost.perpoll.tomtom");
        DateTimeOffset now = DateTimeOffset.UtcNow;

        return records
            .GroupBy(pr => (RouteProvider)pr.Route!.Provider)
            .Select(g =>
            {
                RouteProvider provider = g.Key;
                double costPerPoll = provider == RouteProvider.TomTom ? tomtomCost : googleCost;
                return new PollCostSummaryDto(
                    AsOfUtc: now,
                    Provider: provider,
                    TotalPollCount: g.Count(),
                    TotalEstimatedCostUsd: g.Count() * costPerPoll);
            })
            .ToList();
    }
}
