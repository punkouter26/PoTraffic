using PoTraffic.API.Infrastructure.Storage;



namespace PoTraffic.API.Features.Account;

// FR-031: GDPR Art. 17 — hard delete all user data on request
public sealed record DeleteAccountCommand(UserId UserId) : IRequest<bool>;

public sealed class DeleteAccountCommandHandler : IRequestHandler<DeleteAccountCommand, bool>
{
    private readonly TableStorageContext _db;

    public DeleteAccountCommandHandler(TableStorageContext db) => _db = db;

    public async Task<bool> Handle(DeleteAccountCommand command, CancellationToken ct)
    {
        User? user = _db.Users.FirstOrDefault(u => u.Id == command.UserId);

        if (user is null) return false;

        // Hard delete of everything the user owns, persisted. This used to remove only the
        // in-memory User and never save: the account came back on restart and its routes,
        // samples and alerts were never deleted at all. A deleted route also ends its poll
        // chain — PollRouteJob stops when the route is gone.
        HashSet<RouteId> routeIds = _db.GetUserRouteIds(command.UserId);
        _db.RemoveRange(_db.Polls.Where(p => routeIds.Contains(p.RouteId)).ToList());
        _db.RemoveRange(_db.Sessions.Where(s => routeIds.Contains(s.RouteId)).ToList());
        _db.RemoveRange(_db.Windows.Where(w => routeIds.Contains(w.RouteId)).ToList());
        _db.RemoveRange(_db.Alerts.Where(a => a.UserId == command.UserId).ToList());
        _db.RemoveRange(_db.PushSubscriptions.Where(s => s.UserId == command.UserId).ToList());
        _db.RemoveRange(_db.Routes.Where(r => r.UserId == command.UserId).ToList());
        _db.Remove(user);
        await _db.SaveChangesAsync(ct);

        return true;
    }
}
