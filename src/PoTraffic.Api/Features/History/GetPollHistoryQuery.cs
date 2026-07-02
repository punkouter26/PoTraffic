using PoTraffic.Api.Infrastructure.Storage;


using PoTraffic.Shared.DTOs.History;

using PoTraffic.Shared.DTOs.Routes;

namespace PoTraffic.Api.Features.History;

public sealed record GetPollHistoryQuery(
    Guid RouteId,
    Guid UserId,
    int Page,
    int PageSize,
    DateTime? SinceUtc = null) : IRequest<PagedResult<PollRecordDto>>;

public sealed class GetPollHistoryQueryHandler
    : IRequestHandler<GetPollHistoryQuery, PagedResult<PollRecordDto>>
{
    private readonly TableStorageContext _db;

    public GetPollHistoryQueryHandler(TableStorageContext db)
    {
        _db = db;
    }

    public async Task<PagedResult<PollRecordDto>> Handle(
        GetPollHistoryQuery query,
        CancellationToken ct)
    {
        int skip = (query.Page - 1) * query.PageSize;

        // Ownership check (prevents cross-user IDOR). TableStorageContext has no
        // navigation property, so resolve the route explicitly rather than via
        // the old `p.Route.UserId` access. The route also carries the Provider
        // used to project each DTO below.
        EntityRoute? route = _db.GetOwnedRoute(query.RouteId, query.UserId);
        if (route is null)
            return new PagedResult<PollRecordDto>(query.Page, query.PageSize, 0, new List<PollRecordDto>());

        var baseQuery = _db.PollRecords
            .Where(p => p.RouteId == query.RouteId
                && !p.IsDeleted
                && (query.SinceUtc == null || p.PolledAt >= query.SinceUtc))
            .OrderByDescending(p => p.PolledAt);

        int total = baseQuery.Count();

        List<PollRecordDto> items = baseQuery
            .Skip(skip)
            .Take(query.PageSize)
            .Select(p => new PollRecordDto(
                p.Id,
                p.SessionId,
                p.PolledAt,
                p.TravelDurationSeconds,
                p.DistanceMetres,
                (PoTraffic.Shared.Enums.RouteProvider)route.Provider,
                p.IsRerouted))
            .ToList();

        return new PagedResult<PollRecordDto>(query.Page, query.PageSize, total, items);
    }
}
