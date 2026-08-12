using PoTraffic.API.Features.Config;
using PoTraffic.API.Infrastructure.Storage;
using PoTraffic.Shared.DTOs.Account;
using PoTraffic.Shared.Enums;

namespace PoTraffic.API.Features.Account;

public sealed record GetQuotaQuery(UserId UserId) : IRequest<QuotaDto?>;

public sealed class GetQuotaHandler : IRequestHandler<GetQuotaQuery, QuotaDto?>
{
    private readonly TableStorageContext _db;

    public GetQuotaHandler(TableStorageContext db) => _db = db;

    public async Task<QuotaDto?> Handle(GetQuotaQuery query, CancellationToken ct)
    {
        // Check user exists
        bool userExists = _db.Users.Any(u => u.Id == query.UserId);
        if (!userExists) return null;

        // Load daily quota limit from SystemConfiguration
        string? limitValue = _db.SystemConfigurations
            .Where(c => c.Key == "quota.daily.default")
            .Select(c => c.Value)
            .FirstOrDefault();

        int dailyLimit = int.TryParse(limitValue, out int lim) ? lim : 10;

        // Count today's sessions for this user's routes
        DateTimeOffset dayStart = DateTimeOffset.UtcNow.Date;
        DateTimeOffset dayEnd = dayStart.AddDays(1);

        // Resolve the user's route IDs explicitly — TableStorageContext has no
        // navigation property to back the old `s.Route.UserId` access.
        HashSet<RouteId> userRouteIds = _db.GetUserRouteIds(query.UserId);

        // Count today's sessions only. The previous `>= today && <= dayEnd` range was
        // inclusive of dayEnd (tomorrow), over-counting future-dated sessions; this matches
        // StartWindowCommand's `SessionDate == today` so the displayed quota equals what is enforced.
        DateOnly today = DateOnly.FromDateTime(dayStart.UtcDateTime);
        int usedToday = _db.MonitoringSessions
            .Count(s => userRouteIds.Contains(s.RouteId) && s.SessionDate == today);

        int remaining = Math.Max(0, dailyLimit - usedToday);

        // Reset time = midnight UTC next day
        DateTimeOffset resetsAt = dayEnd;

        // Cost transparency (#8): today's provider spend, priced per-poll by each route's
        // provider (rates live in SystemConfiguration). Monthly projection extrapolates today.
        PollCostRates rates = PollCostRates.Load(_db, googleMapsFallback: 0.005m, tomTomFallback: 0.004m);

        // One pass over the poll table — a per-route Count() would rescan the whole
        // (global) poll list once per route the user owns.
        Dictionary<RouteId, int> pollsByRoute = _db.Polls
            .Where(p => userRouteIds.Contains(p.RouteId) && !p.IsDeleted
                        && DateOnly.FromDateTime(p.PolledAt.UtcDateTime) == today)
            .GroupBy(p => p.RouteId)
            .ToDictionary(g => g.Key, g => g.Count());

        int pollsToday = 0;
        decimal costToday = 0m;
        foreach (EntityRoute route in _db.Routes.Where(r => userRouteIds.Contains(r.Id)))
        {
            // The sample route's samples were generated, not bought (#10). Counting them
            // would report spend against an account that never called a provider.
            if (route.IsDemo) continue;
            if (!pollsByRoute.TryGetValue(route.Id, out int polls)) continue;
            pollsToday += polls;
            costToday += polls * rates.For((RouteProvider)route.Provider);
        }

        return new QuotaDto(
            DailyLimit: dailyLimit,
            UsedToday: usedToday,
            Remaining: remaining,
            ResetsAtUtc: resetsAt,
            PollsToday: pollsToday,
            EstimatedCostTodayUsd: Math.Round(costToday, 4),
            ProjectedMonthlyCostUsd: Math.Round(costToday * 30, 2));
    }
}
