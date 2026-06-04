using PoTraffic.Api.Infrastructure.Scheduling;
using PoTraffic.Api.Infrastructure.Storage;

using MediatR;

using Microsoft.Extensions.Logging;



using PoTraffic.Shared.Enums;

namespace PoTraffic.Api.Features.Routes;

public sealed record DeleteRouteCommand(
    Guid RouteId,
    Guid UserId) : IRequest<bool>;

public sealed class DeleteRouteCommandHandler : IRequestHandler<DeleteRouteCommand, bool>
{
    private readonly TableStorageContext _db;
    private readonly IJobScheduler _scheduler;
    private readonly ILogger<DeleteRouteCommandHandler> _logger;

    public DeleteRouteCommandHandler(
        TableStorageContext db,
        IJobScheduler scheduler,
        ILogger<DeleteRouteCommandHandler> logger)
    {
        _db = db;
        _scheduler = scheduler;
        _logger = logger;
    }

    public async Task<bool> Handle(DeleteRouteCommand cmd, CancellationToken ct)
    {
        EntityRoute? route = _db.Routes
            .FirstOrDefault(r => r.Id == cmd.RouteId && r.UserId == cmd.UserId
                && r.MonitoringStatus != (int)MonitoringStatus.Deleted);

        if (route is null)
            return false;

        // Cancel any running job chain
        if (route.JobChainId is not null)
        {
            _scheduler.Cancel(route.JobChainId);
            _logger.LogInformation("Cancelled job chain {JobId} for soft-deleted route {RouteId}",
                route.JobChainId, route.Id);
        }

        // Soft-delete: preserve data for reporting / retention period
        route.MonitoringStatus = (int)MonitoringStatus.Deleted;
        route.JobChainId = null;

        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Route {RouteId} soft-deleted by user {UserId}", route.Id, cmd.UserId);
        return true;
    }
}
