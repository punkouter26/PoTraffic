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
        decimal googleRate = ResolveRate("cost.perpoll.googlemaps", 0.005m);
        decimal tomtomRate = ResolveRate("cost.perpoll.tomtom", 0.004m);
        DateOnly todayDate = today;

        int pollsToday = 0;
        decimal costToday = 0m;
        foreach (var route in _db.Routes.Where(r => userRouteIds.Contains(r.Id)))
        {
            int polls = _db.Polls.Count(p =>
                p.RouteId == route.Id && !p.IsDeleted
                && DateOnly.FromDateTime(p.PolledAt.UtcDateTime) == todayDate);
            pollsToday += polls;
            costToday += polls * (route.Provider == (int)RouteProvider.TomTom ? tomtomRate : googleRate);
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

    private decimal ResolveRate(string key, decimal fallback)
    {
        string? value = _db.SystemConfigurations
            .Where(c => c.Key == key)
            .Select(c => c.Value)
            .FirstOrDefault();
        return decimal.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out decimal rate)
            ? rate
            : fallback;
    }
}
