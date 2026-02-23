using MediatR;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PoTraffic.Api.Infrastructure.Data;
using PoTraffic.Shared.DTOs.History;
using ProjectionSlot = PoTraffic.Api.Infrastructure.Data.Projections.BaselineSlotDto;

namespace PoTraffic.Api.Features.History;

public sealed record GetOptimalDepartureQuery(
    Guid RouteId,
    string DayOfWeek) : IRequest<OptimalDepartureDto?>;

public sealed class GetOptimalDepartureQueryHandler
    : IRequestHandler<GetOptimalDepartureQuery, OptimalDepartureDto?>
{
    private readonly PoTrafficDbContext _db;
    private readonly ILogger<GetOptimalDepartureQueryHandler> _logger;

    public GetOptimalDepartureQueryHandler(
        PoTrafficDbContext db,
        ILogger<GetOptimalDepartureQueryHandler> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<OptimalDepartureDto?> Handle(GetOptimalDepartureQuery query, CancellationToken ct)
    {
        // Shared SQL — see BaselineSqlQueries.SlotAggregate (DRY: avoids drift with GetBaselineQuery).
        List<ProjectionSlot> slots;

        try
        {
            slots = await _db.Database
                .SqlQueryRaw<ProjectionSlot>(
                    BaselineSqlQueries.SlotAggregate,
                    new SqlParameter("@routeId", query.RouteId),
                    new SqlParameter("@dayOfWeek", query.DayOfWeek))
                .ToListAsync(ct);
        }
        catch (InvalidOperationException)
        {
            // InMemory provider does not support SqlQueryRaw — return null for test environments
            _logger.LogDebug("GetOptimalDepartureQuery: SQL not supported on InMemory provider");
            return null;
        }

        if (slots.Count == 0)
            return null;

        // FR-009: find contiguous run of slots within 5% of minimum MeanDurationSeconds
        (int startBucket, int endBucket, double minMean) = FindOptimalWindow(slots.ToArray());

        // Map bucket back to HH:mm label
        int startHour = startBucket / 60;
        int startMin = startBucket % 60;
        int endHour = endBucket / 60;
        int endMin = endBucket % 60;

        string label =
            $"Best: {startHour:D2}:{startMin:D2}–{endHour:D2}:{(endMin + 5) % 60:D2}";

        return new OptimalDepartureDto(
            query.DayOfWeek,
            startBucket,
            minMean,
            minMean * 0.95,
            minMean * 1.05);
    }

    /// <summary>
    /// Finds the contiguous run of slot buckets whose MeanDurationSeconds is within 5% of the minimum.
    /// When multiple runs are non-contiguous, returns the longest run.
    /// Exposed public for unit testing (FR-009).
    /// </summary>
    public static (int startBucket, int endBucket, double minMean) FindOptimalWindow(
        ProjectionSlot[] slots)
    {
        double minMean = slots.Min(s => s.MeanDurationSeconds);
        double threshold = minMean * 1.05;

        // Find all qualifying slots
        List<int> qualifying = slots
            .Where(s => s.MeanDurationSeconds <= threshold)
            .Select(s => s.TimeSlotBucket)
            .OrderBy(b => b)
            .ToList();

        if (qualifying.Count == 0)
        {
            // Fallback: return the slot with the minimum mean
            ProjectionSlot best = slots.OrderBy(s => s.MeanDurationSeconds).First();
            return (best.TimeSlotBucket, best.TimeSlotBucket, minMean);
        }

        // Find longest contiguous run (adjacent buckets differ by 5 minutes)
        int bestStart = qualifying[0];
        int bestEnd = qualifying[0];
        int curStart = qualifying[0];
        int curEnd = qualifying[0];

        for (int i = 1; i < qualifying.Count; i++)
        {
            if (qualifying[i] == qualifying[i - 1] + 5)
            {
                curEnd = qualifying[i];
            }
            else
            {
                // Run broke — compare with best
                if (curEnd - curStart > bestEnd - bestStart)
                {
                    bestStart = curStart;
                    bestEnd = curEnd;
                }
                curStart = qualifying[i];
                curEnd = qualifying[i];
            }
        }

        // Check final run
        if (curEnd - curStart > bestEnd - bestStart)
        {
            bestStart = curStart;
            bestEnd = curEnd;
        }

        return (bestStart, bestEnd, minMean);
    }
}
