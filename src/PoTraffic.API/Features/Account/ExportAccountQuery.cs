using PoTraffic.API.Features.Alerts;
using PoTraffic.API.Infrastructure.Storage;

namespace PoTraffic.API.Features.Account;

/// <summary>
/// GDPR Art. 20 — everything stored about the user, as one JSON document. Entities are
/// exported as stored (navigation properties are [JsonIgnore]); push subscriptions are
/// counted rather than exported, because their keys are delivery credentials, not data
/// about the user.
/// </summary>
public sealed record ExportAccountQuery(UserId UserId);

public sealed record AccountExport(
    DateTimeOffset ExportedAt,
    User User,
    List<EntityRoute> Routes,
    List<MonitoringWindow> MonitoringWindows,
    List<MonitoringSession> MonitoringSessions,
    List<PollRecord> PollRecords,
    List<Alert> Alerts,
    int PushSubscriptionCount);

public sealed class ExportAccountHandler(TableStorageContext db)
{
    public Task<AccountExport?> Handle(ExportAccountQuery query, CancellationToken ct)
    {
        User? user = db.Users.FirstOrDefault(u => u.Id == query.UserId);
        if (user is null)
            return Task.FromResult<AccountExport?>(null);

        HashSet<RouteId> routeIds = db.GetUserRouteIds(query.UserId);
        return Task.FromResult<AccountExport?>(new AccountExport(
            DateTimeOffset.UtcNow,
            user,
            [.. db.Routes.Where(r => r.UserId == query.UserId)],
            [.. db.Windows.Where(w => routeIds.Contains(w.RouteId))],
            [.. db.Sessions.Where(s => routeIds.Contains(s.RouteId))],
            [.. db.Polls.Where(p => routeIds.Contains(p.RouteId)).OrderBy(p => p.PolledAt)],
            [.. db.Alerts.Where(a => a.UserId == query.UserId)],
            db.PushSubscriptions.Count(s => s.UserId == query.UserId)));
    }
}
