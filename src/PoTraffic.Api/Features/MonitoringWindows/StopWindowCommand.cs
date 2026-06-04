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
        // Load session + route, verify ownership
        MonitoringSession? session = _db.MonitoringSessions
            .FirstOrDefault(s => s.Id == cmd.SessionId
                && s.Route.UserId == cmd.UserId
                && s.State == (int)SessionState.Active);

        if (session is null)
            return false;

        // Transition session to Completed
        session.State = (int)SessionState.Completed;

        // Cancel the job chain
        if (session.Route.JobChainId is not null)
        {
            _scheduler.Cancel(session.Route.JobChainId);
            _logger.LogInformation(
                "Cancelled job chain {JobId} on stop for route {RouteId}",
                session.Route.JobChainId, session.RouteId);
            session.Route.JobChainId = null;
        }

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Session {SessionId} stopped for route {RouteId}", cmd.SessionId, session.RouteId);
        return true;
    }
}
