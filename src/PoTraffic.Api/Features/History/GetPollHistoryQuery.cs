using MediatR;
using Microsoft.EntityFrameworkCore;
using PoTraffic.Api.Infrastructure.Data;
using PoTraffic.Shared.DTOs.History;
using PoTraffic.Shared.DTOs.Routes;

namespace PoTraffic.Api.Features.History;

public sealed record GetPollHistoryQuery(
    Guid RouteId,
    Guid UserId,
    int Page,
    int PageSize) : IRequest<PagedResult<PollRecordDto>>;

public sealed class GetPollHistoryQueryHandler
    : IRequestHandler<GetPollHistoryQuery, PagedResult<PollRecordDto>>
{
    private readonly PoTrafficDbContext _db;

    public GetPollHistoryQueryHandler(PoTrafficDbContext db)
    {
        _db = db;
    }

    public async Task<PagedResult<PollRecordDto>> Handle(
        GetPollHistoryQuery query,
        CancellationToken ct)
    {
        int skip = (query.Page - 1) * query.PageSize;

        var baseQuery = _db.PollRecords
            .Where(p => p.RouteId == query.RouteId && !p.IsDeleted)
            .OrderByDescending(p => p.PolledAt);

        int total = await baseQuery.CountAsync(ct);

        List<PollRecordDto> items = await baseQuery
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
            .ToListAsync(ct);

        return new PagedResult<PollRecordDto>(query.Page, query.PageSize, total, items);
    }
}
