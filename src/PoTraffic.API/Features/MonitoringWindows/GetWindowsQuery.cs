using PoTraffic.API.Infrastructure.Storage;


using PoTraffic.Shared.DTOs.Routes;

namespace PoTraffic.API.Features.MonitoringWindows;

/// <summary>Efficient direct query for a route's windows — avoids loading all user routes.</summary>
public sealed record GetWindowsQuery(RouteId RouteId, UserId UserId) : IRequest<IReadOnlyList<MonitoringWindowDto>?>;

public sealed class GetWindowsQueryHandler(TableStorageContext db)
    : IRequestHandler<GetWindowsQuery, IReadOnlyList<MonitoringWindowDto>?>
{
    public async Task<IReadOnlyList<MonitoringWindowDto>?> Handle(GetWindowsQuery q, CancellationToken ct)
    {
        if (!db.OwnsRoute(q.RouteId, q.UserId)) return null;

        return db.MonitoringWindows
            .Where(w => w.RouteId == q.RouteId)
            .OrderBy(w => w.StartTime)
            .Select(w => w.ToDto())
            .ToList();
    }
}
