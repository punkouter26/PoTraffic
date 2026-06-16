using PoTraffic.Api.Infrastructure.Scheduling;
using PoTraffic.Api.Infrastructure.Storage;

using MediatR;

using Microsoft.Extensions.Logging;



using PoTraffic.Shared.Enums;

namespace PoTraffic.Api.Features.MonitoringWindows;

public sealed record StopWindowCommand(
    Guid SessionId,
    Guid UserId) : IRequest<bool>;

public sealed class StopWindowCommandHandler : IRequestHandler<StopWindowCommand, bool>
{
    private readonly TableStorageContext _db;
    private readonly IJobScheduler _scheduler;
    private readonly ILogger<StopWindowCommandHandler> _logger;

    public StopWindowCommandHandler(
        TableStorageContext db,
        IJobScheduler scheduler,
        ILogger<StopWindowCommandHandler> logger)
    {
        _db = db;
        _scheduler = scheduler;
        _logger = logger;
    }

    public async Task<bool> Handle(StopWindowCommand cmd, CancellationToken ct)
    {
        // Load session, then resolve the owning route explicitly — TableStorageContext
        // has no navigation property to back the old `s.Route.UserId` access.
        MonitoringSession? session = _db.MonitoringSessions
            .FirstOrDefault(s => s.Id == cmd.SessionId
                && s.State == (int)SessionState.Active);

        if (session is null)
            return false;

        EntityRoute? route = _db.GetOwnedRoute(session.RouteId, cmd.UserId);
        if (route is null)
            return false;

        // Transition session to Completed
        session.State = (int)SessionState.Completed;

        // Cancel the job chain
        if (route.JobChainId is not null)
        {
            _scheduler.Cancel(route.JobChainId);
            _logger.LogInformation(
                "Cancelled job chain {JobId} on stop for route {RouteId}",
                route.JobChainId, session.RouteId);
            route.JobChainId = null;
        }

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Session {SessionId} stopped for route {RouteId}", cmd.SessionId, session.RouteId);
        return true;
    }
}
