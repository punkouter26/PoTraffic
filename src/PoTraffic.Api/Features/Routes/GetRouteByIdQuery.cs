using PoTraffic.Api.Infrastructure.Storage;


using PoTraffic.Shared.DTOs.Routes;

using PoTraffic.Shared.Enums;

namespace PoTraffic.Api.Features.Routes;

/// <summary>
/// Fetches a single route by ID, verifying ownership in a single DB round-trip.
/// Replaces the inefficient pattern of loading all user routes then filtering in memory.
/// </summary>
public sealed record GetRouteByIdQuery(Guid RouteId, Guid UserId) : IRequest<RouteDto?>;

public sealed class GetRouteByIdQueryHandler(TableStorageContext db)
    : IRequestHandler<GetRouteByIdQuery, RouteDto?>
{
    public async Task<RouteDto?> Handle(GetRouteByIdQuery q, CancellationToken ct)
    {
        EntityRoute? route = db.Routes
            .FirstOrDefault(r =>
                r.Id == q.RouteId
                && r.UserId == q.UserId
                && r.MonitoringStatus != (int)MonitoringStatus.Deleted);

        return route is null ? null : CreateRouteCommandHandler.MapToDto(route);
    }
}
