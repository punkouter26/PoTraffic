using MediatR;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PoTraffic.Api.Infrastructure.Data;
using PoTraffic.Shared.DTOs.History;
using ProjectionSlot = PoTraffic.Api.Infrastructure.Data.Projections.BaselineSlotDto;

namespace PoTraffic.Api.Features.History;

public sealed record GetBaselineQuery(
    Guid RouteId,
    string DayOfWeek) : IRequest<BaselineResponse>;

public sealed class GetBaselineQueryHandler
    : IRequestHandler<GetBaselineQuery, BaselineResponse>
{
    private readonly PoTrafficDbContext _db;
    private readonly ILogger<GetBaselineQueryHandler> _logger;

    public GetBaselineQueryHandler(PoTrafficDbContext db, ILogger<GetBaselineQueryHandler> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<BaselineResponse> Handle(GetBaselineQuery query, CancellationToken ct)
    {
        // Shared SQL — see BaselineSqlQueries.SlotAggregate (DRY: avoids drift with GetOptimalDepartureQuery).
        // LINQ cannot produce STDEV(); raw SQL is intentional per §6.2 of data-model.md.
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
            // InMemory provider does not support SqlQueryRaw — return empty baseline for test environments
            _logger.LogDebug(
                "GetBaselineQuery: SQL not supported on InMemory provider (test env) — returning empty baseline");
            slots = [];
        }

        return new BaselineResponse(
            query.RouteId,
            query.DayOfWeek,
            slots.FirstOrDefault()?.SessionCount ?? 0,
            slots.Select(s => new PoTraffic.Shared.DTOs.History.BaselineSlotDto(
                s.DayOfWeek,
                s.TimeSlotBucket,
                s.MeanDurationSeconds,
                s.StdDevDurationSeconds,
                s.SessionCount)).ToList());
    }
}
