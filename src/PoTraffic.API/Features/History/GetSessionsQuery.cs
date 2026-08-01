using PoTraffic.API.Infrastructure.Storage;


using PoTraffic.Shared.DTOs.History;

using PoTraffic.Shared.Enums;

namespace PoTraffic.API.Features.History;

public sealed record GetSessionsQuery(
    RouteId RouteId,
    UserId UserId) : IRequest<IReadOnlyList<SessionDto>>;

public sealed class GetSessionsQueryHandler
    : IRequestHandler<GetSessionsQuery, IReadOnlyList<SessionDto>>
{
    private readonly TableStorageContext _db;

    public GetSessionsQueryHandler(TableStorageContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<SessionDto>> Handle(
        GetSessionsQuery query,
        CancellationToken ct)
    {
        // Ownership check — TableStorageContext has no navigation properties, so the
        // old `s.Route.UserId` access NREs. Resolve ownership explicitly instead.
        if (!_db.OwnsRoute(query.RouteId, query.UserId))
            return Array.Empty<SessionDto>();

        return _db.MonitoringSessions
            .Where(s => s.RouteId == query.RouteId)
            .OrderByDescending(s => s.SessionDate)
            .Select(s => new SessionDto(
                s.Id,
                s.RouteId,
                s.SessionDate,
                (SessionState)s.State,
                s.FirstPollAt,
                s.LastPollAt,
                s.PollCount,
                s.QuotaConsumed,
                s.IsHolidayExcluded))
            .ToList();
    }
}
