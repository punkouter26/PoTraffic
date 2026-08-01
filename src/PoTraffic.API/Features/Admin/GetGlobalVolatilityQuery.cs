using PoTraffic.API.Infrastructure.Storage;

using Microsoft.Extensions.Logging;


using PoTraffic.Shared.DTOs.Admin;
using PoTraffic.Shared.Enums;

namespace PoTraffic.API.Features.Admin;

// FR-024: Global volatility aggregation across all active routes, grouped by
// DayOfWeek × 5-minute bucket × Provider. Post-refactor: pure LINQ over the
// in-process Table Storage backend.
public sealed record GetGlobalVolatilityQuery : IRequest<IReadOnlyList<GlobalVolatilitySlotDto>>;

public sealed class GetGlobalVolatilityHandler : IRequestHandler<GetGlobalVolatilityQuery, IReadOnlyList<GlobalVolatilitySlotDto>>
{
    private readonly TableStorageContext _db;
    private readonly ILogger<GetGlobalVolatilityHandler> _logger;

    public GetGlobalVolatilityHandler(TableStorageContext db, ILogger<GetGlobalVolatilityHandler> logger)
    {
        _db = db;
        _logger = logger;
    }

    public Task<IReadOnlyList<GlobalVolatilitySlotDto>> Handle(GetGlobalVolatilityQuery query, CancellationToken ct)
    {
        // Snapshot to lists so the LINQ pipeline can run multiple times.
        List<PollRecord> polls = _db.Polls.Where(p => !p.IsDeleted).ToList();
        Dictionary<RouteId, EntityRoute> routesById = _db.Routes.ToDictionary(r => r.Id);

        return Task.FromResult(VolatilityAggregator.Aggregate(polls, routesById));
    }
}
