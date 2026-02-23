using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PoTraffic.Api.Infrastructure.Data;

namespace PoTraffic.Api.Features.MonitoringWindows;

public sealed record DeleteWindowCommand(
    Guid WindowId,
    Guid UserId) : IRequest<bool>;

public sealed class DeleteWindowCommandHandler : IRequestHandler<DeleteWindowCommand, bool>
{
    private readonly PoTrafficDbContext _db;
    private readonly ILogger<DeleteWindowCommandHandler> _logger;

    public DeleteWindowCommandHandler(
        PoTrafficDbContext db,
        ILogger<DeleteWindowCommandHandler> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<bool> Handle(DeleteWindowCommand cmd, CancellationToken ct)
    {
        var window = await _db.MonitoringWindows
            .Include(w => w.Route)
            .FirstOrDefaultAsync(w => w.Id == cmd.WindowId
                && w.Route.UserId == cmd.UserId
                && w.IsActive, ct);

        if (window is null)
            return false;

        window.IsActive = false;
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("MonitoringWindow {WindowId} deleted for route {RouteId}", window.Id, window.RouteId);
        return true;
    }
}
