using Hangfire;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PoTraffic.Api.Infrastructure.Data;

using PoTraffic.Shared.Enums;

namespace PoTraffic.Api.Features.Routes;

public sealed record DeleteRouteCommand(
    Guid RouteId,
    Guid UserId) : IRequest<bool>;

public sealed class DeleteRouteCommandHandler : IRequestHandler<DeleteRouteCommand, bool>
{
    private readonly PoTrafficDbContext _db;
    private readonly IBackgroundJobClient _jobClient;
    private readonly ILogger<DeleteRouteCommandHandler> _logger;

    public DeleteRouteCommandHandler(
        PoTrafficDbContext db,
        IBackgroundJobClient jobClient,
        ILogger<DeleteRouteCommandHandler> logger)
    {
        _db = db;
        _jobClient = jobClient;
        _logger = logger;
    }

    public async Task<bool> Handle(DeleteRouteCommand cmd, CancellationToken ct)
    {
        EntityRoute? route = await _db.Routes
            .FirstOrDefaultAsync(r => r.Id == cmd.RouteId && r.UserId == cmd.UserId
                && r.MonitoringStatus != (int)MonitoringStatus.Deleted, ct);

        if (route is null)
            return false;

        // Cancel any running Hangfire job chain
        if (route.HangfireJobChainId is not null)
        {
            _jobClient.Delete(route.HangfireJobChainId);
            _logger.LogInformation("Cancelled Hangfire job chain {JobId} for soft-deleted route {RouteId}",
                route.HangfireJobChainId, route.Id);
        }

        // Soft-delete: preserve data for reporting / retention period
        route.MonitoringStatus = (int)MonitoringStatus.Deleted;
        route.HangfireJobChainId = null;

        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Route {RouteId} soft-deleted by user {UserId}", route.Id, cmd.UserId);
        return true;
    }
}
