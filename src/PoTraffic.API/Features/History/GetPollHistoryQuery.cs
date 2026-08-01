using PoTraffic.API.Infrastructure.Storage;


using PoTraffic.Shared.DTOs.History;

using PoTraffic.Shared.DTOs.Routes;

namespace PoTraffic.API.Features.History;

public sealed record GetPollHistoryQuery(
    RouteId RouteId,
    UserId UserId,
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

        // Materialise once: the filter runs over the whole (global) poll list, so
        // counting and paging off the same IQueryable would scan — and re-sort — it twice.
        List<PollRecord> matching = [.. _db.PollRecords
            .Where(p => p.RouteId == query.RouteId
                && !p.IsDeleted
                && (query.SinceUtc == null || p.PolledAt >= query.SinceUtc))];

        int total = matching.Count;

        List<PollRecordDto> items = matching
            .OrderByDescending(p => p.PolledAt)
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
