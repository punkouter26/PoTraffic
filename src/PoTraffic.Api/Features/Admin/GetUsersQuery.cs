using FluentValidation;
using PoTraffic.Api.Infrastructure.Storage;




using PoTraffic.Shared.DTOs.Admin;

using PoTraffic.Shared.Enums;

namespace PoTraffic.Api.Features.Admin;

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
        Dictionary<Guid, List<EntityRoute>> routesByUser = _db.Routes
            .Where(r => r.MonitoringStatus != (int)MonitoringStatus.Deleted)
            .GroupBy(r => r.UserId)
            .ToDictionary(g => g.Key, g => g.ToList());

        // Load today's poll records across all routes
        Dictionary<Guid, List<PollRecord>> pollsByRoute = _db.PollRecords
            .Where(p => p.PolledAt >= dayStart && p.PolledAt < dayEnd && !p.IsDeleted)
            .GroupBy(p => p.RouteId)
            .ToDictionary(g => g.Key, g => g.ToList());

        List<SystemConfiguration> configs = _db.SystemConfigurations
            .Where(c => c.Key.StartsWith("cost.perpoll."))
            .ToList();

        double googleCost = GetCost(configs, "cost.perpoll.googlemaps");
        double tomtomCost = GetCost(configs, "cost.perpoll.tomtom");

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
                    double costPerPoll = grp.Key == RouteProvider.TomTom ? tomtomCost : googleCost;
                    return new ProviderBreakdownDto(grp.Key, pollCount, pollCount * costPerPoll);
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

    private static double GetCost(List<SystemConfiguration> configs, string key)
    {
        string? value = configs.FirstOrDefault(c => c.Key == key)?.Value;
        return double.TryParse(value, out double result) ? result : 0.0;
    }
}
