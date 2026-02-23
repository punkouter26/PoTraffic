using Hangfire;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PoTraffic.Api.Infrastructure.Data;

using PoTraffic.Shared.Enums;

namespace PoTraffic.Api.Features.MonitoringWindows;

public sealed record StopWindowCommand(
    Guid SessionId,
    Guid UserId) : IRequest<bool>;

public sealed class StopWindowCommandHandler : IRequestHandler<StopWindowCommand, bool>
{
    private readonly PoTrafficDbContext _db;
    private readonly IBackgroundJobClient _jobClient;
    private readonly ILogger<StopWindowCommandHandler> _logger;

    public StopWindowCommandHandler(
        PoTrafficDbContext db,
        IBackgroundJobClient jobClient,
        ILogger<StopWindowCommandHandler> logger)
    {
        _db = db;
        _jobClient = jobClient;
        _logger = logger;
    }

    public async Task<bool> Handle(StopWindowCommand cmd, CancellationToken ct)
    {
        // Load session + route, verify ownership
        MonitoringSession? session = await _db.MonitoringSessions
            .Include(s => s.Route)
            .FirstOrDefaultAsync(s => s.Id == cmd.SessionId
                && s.Route.UserId == cmd.UserId
                && s.State == (int)SessionState.Active, ct);

        if (session is null)
            return false;

        // Transition session to Completed
        session.State = (int)SessionState.Completed;

        // Cancel the Hangfire job chain
        if (session.Route.HangfireJobChainId is not null)
        {
            _jobClient.Delete(session.Route.HangfireJobChainId);
            _logger.LogInformation(
                "Cancelled Hangfire job chain {JobId} on stop for route {RouteId}",
                session.Route.HangfireJobChainId, session.RouteId);
            session.Route.HangfireJobChainId = null;
        }

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Session {SessionId} stopped for route {RouteId}", cmd.SessionId, session.RouteId);
        return true;
    }
}
