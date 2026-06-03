using MediatR;
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

        var baseQuery = _db.PollRecords
            .Where(p => p.RouteId == query.RouteId
                && p.Route.UserId == query.UserId   // ownership: prevents cross-user IDOR
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
                (PoTraffic.Shared.Enums.RouteProvider)p.Route.Provider,
                p.IsRerouted))
            .ToList();

        return new PagedResult<PollRecordDto>(query.Page, query.PageSize, total, items);
    }
}
