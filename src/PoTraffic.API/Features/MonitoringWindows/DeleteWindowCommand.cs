using PoTraffic.API.Infrastructure.Storage;

using Microsoft.Extensions.Logging;


namespace PoTraffic.API.Features.MonitoringWindows;

public sealed record DeleteWindowCommand(
    WindowId WindowId,
    UserId UserId) : IRequest<bool>;

public sealed class DeleteWindowCommandHandler : IRequestHandler<DeleteWindowCommand, bool>
{
    private readonly TableStorageContext _db;
    private readonly ILogger<DeleteWindowCommandHandler> _logger;

    public DeleteWindowCommandHandler(
        TableStorageContext db,
        ILogger<DeleteWindowCommandHandler> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<bool> Handle(DeleteWindowCommand cmd, CancellationToken ct)
    {
        var window = _db.MonitoringWindows
            .FirstOrDefault(w => w.Id == cmd.WindowId && w.IsActive);

        if (window is null)
            return false;

        // Verify ownership via the route (avoids null navigation property in in-memory context)
        if (!_db.OwnsRoute(window.RouteId, cmd.UserId))
            return false;

        window.IsActive = false;
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("MonitoringWindow {WindowId} deleted for route {RouteId}", window.Id, window.RouteId);
        return true;
    }
}
