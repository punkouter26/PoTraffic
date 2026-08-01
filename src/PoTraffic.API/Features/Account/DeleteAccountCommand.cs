using PoTraffic.API.Infrastructure.Storage;



namespace PoTraffic.API.Features.Account;

// FR-031: GDPR Art. 17 — hard delete all user data on request
public sealed record DeleteAccountCommand(UserId UserId) : IRequest<bool>;

public sealed class DeleteAccountCommandHandler : IRequestHandler<DeleteAccountCommand, bool>
{
    private readonly TableStorageContext _db;

    public DeleteAccountCommandHandler(TableStorageContext db) => _db = db;

    public Task<bool> Handle(DeleteAccountCommand command, CancellationToken ct)
    {
        User? user = _db.Users.FirstOrDefault(u => u.Id == command.UserId);

        if (user is null) return Task.FromResult(false);

        // Hard delete — in-process store, no cascade needed.
        _db.Remove(user);

        return Task.FromResult(true);
    }
}
