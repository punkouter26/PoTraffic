using MediatR;
using Microsoft.EntityFrameworkCore;
using PoTraffic.Api.Infrastructure.Data;

using PoTraffic.Shared.DTOs.Routes;
using PoTraffic.Shared.Enums;

namespace PoTraffic.Api.Features.Routes;

public sealed record GetRoutesQuery(
    Guid UserId,
    int Page,
    int PageSize) : IRequest<PagedResult<RouteDto>>;

public sealed class GetRoutesQueryHandler(PoTrafficDbContext db) : IRequestHandler<GetRoutesQuery, PagedResult<RouteDto>>
{
    public async Task<PagedResult<RouteDto>> Handle(GetRoutesQuery q, CancellationToken ct)
    {
        IQueryable<EntityRoute> baseQuery = db.Routes
            .Where(r => r.UserId == q.UserId && r.MonitoringStatus != (int)MonitoringStatus.Deleted)
            .Include(r => r.Windows);

        int total = await baseQuery.CountAsync(ct);

        int page = q.Page < 1 ? 1 : q.Page;
        int pageSize = q.PageSize < 1 ? 20 : q.PageSize > 100 ? 100 : q.PageSize;

        List<EntityRoute> routes = await baseQuery
            .OrderByDescending(r => r.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .AsNoTracking()
            .ToListAsync(ct);

        IReadOnlyList<RouteDto> items = routes
            .Select(CreateRouteCommandHandler.MapToDto)
            .ToList();

        return new PagedResult<RouteDto>(page, pageSize, total, items);
    }
}
