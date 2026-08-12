using PoTraffic.API.Features.Config;
using PoTraffic.API.Infrastructure.Storage;
using PoTraffic.Shared.DTOs.Admin;
using PoTraffic.Shared.Enums;

namespace PoTraffic.API.Features.Admin;

// Query pattern — read-only admin user list with today's usage
public sealed record GetUsersQuery : IRequest<IReadOnlyList<UserDailyUsageDto>>;

public sealed class GetUsersHandler : IRequestHandler<GetUsersQuery, IReadOnlyList<UserDailyUsageDto>>
{
    private readonly TableStorageContext _db;

    public GetUsersHandler(TableStorageContext db) => _db = db;

    public async Task<IReadOnlyList<UserDailyUsageDto>> Handle(GetUsersQuery query, CancellationToken ct)
    {
        DateTimeOffset dayStart = DateTimeOffset.UtcNow.Date;
        DateTimeOffset dayEnd = dayStart.AddDays(1);

        List<User> users = _db.Users.ToList();

        // Routes grouped by owner. TableStorageContext never populates the `User.Routes`
        // navigation collection (it stays the empty default), so reading `u.Routes` below
        // silently yielded zero polls/cost for every user — resolve from _db.Routes instead.
        // Deleted routes are excluded here so the per-user breakdown ignores them.
        Dictionary<UserId, List<EntityRoute>> routesByUser = _db.Routes
            .Where(r => r.MonitoringStatus != (int)MonitoringStatus.Deleted)
            .GroupBy(r => r.UserId)
            .ToDictionary(g => g.Key, g => g.ToList());

        // Load today's poll records across all routes
        Dictionary<RouteId, List<PollRecord>> pollsByRoute = _db.PollRecords
            .Where(p => p.PolledAt >= dayStart && p.PolledAt < dayEnd)
            .GroupBy(p => p.RouteId)
            .ToDictionary(g => g.Key, g => g.ToList());

        PollCostRates rates = PollCostRates.Load(_db);

        return users.Select(u =>
        {
            List<EntityRoute> userRoutes = routesByUser.TryGetValue(u.Id, out var rs) ? rs : [];

            IEnumerable<PollRecord> todayPolls = userRoutes
                .SelectMany(r => pollsByRoute.TryGetValue(r.Id, out var polls) ? polls : []);
            int totalCount = todayPolls.Count();

            var breakdown = userRoutes
                .GroupBy(r => (RouteProvider)r.Provider)
                .Select(grp =>
                {
                    int pollCount = grp
                        .SelectMany(r => pollsByRoute.TryGetValue(r.Id, out var p) ? p : [])
                        .Count();
                    return new ProviderBreakdownDto(grp.Key, pollCount, (double)(pollCount * rates.For(grp.Key)));
                })
                .ToList();

            double totalCost = breakdown.Sum(b => b.EstimatedCostUsd);

            return new UserDailyUsageDto(
                UserId: u.Id,
                Email: u.Email,
                Locale: u.Locale,
                CreatedAt: u.CreatedAt,
                LastLoginAt: u.LastLoginAt,
                TodayPollCount: totalCount,
                TodayEstimatedCostUsd: totalCost,
                ProviderBreakdown: breakdown);
        }).ToList();
    }
}
