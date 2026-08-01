using PoTraffic.API.Infrastructure.Storage;

using Microsoft.Extensions.Logging;


using PoTraffic.Shared.DTOs.Admin;
using PoTraffic.Shared.Enums;

namespace PoTraffic.API.Features.Admin;

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

        List<PollRecord> polls = _db.Polls.Where(p => p.PolledAt >= since && !p.IsDeleted).ToList();
        Dictionary<RouteId, EntityRoute> routesById = _db.Routes.ToDictionary(r => r.Id);

        return Task.FromResult(VolatilityAggregator.Aggregate(polls, routesById));
    }
}
