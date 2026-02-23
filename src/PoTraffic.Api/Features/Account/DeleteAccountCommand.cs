using MediatR;
using Microsoft.EntityFrameworkCore;
using PoTraffic.Api.Infrastructure.Data;


namespace PoTraffic.Api.Features.Account;

// FR-031: GDPR Art. 17 — hard delete all user data on request
public sealed record DeleteAccountCommand(Guid UserId) : IRequest<bool>;

public sealed class DeleteAccountCommandHandler : IRequestHandler<DeleteAccountCommand, bool>
{
    private readonly PoTrafficDbContext _db;

    public DeleteAccountCommandHandler(PoTrafficDbContext db) => _db = db;

    public async Task<bool> Handle(DeleteAccountCommand command, CancellationToken ct)
    {
        User? user = await _db.Users.FindAsync([command.UserId], ct);

        if (user is null) return false;

        // Hard delete — EF cascade handles Routes → MonitoringWindows → PollRecords
        _db.Users.Remove(user);
        await _db.SaveChangesAsync(ct);

        return true;
    }
}
