using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using PoTraffic.Api.Infrastructure.Data;

using PoTraffic.Shared.DTOs.Admin;
using PoTraffic.Shared.Enums;

namespace PoTraffic.Api.Features.Admin;

// Query pattern — read-only admin user list with today's usage
public sealed record GetUsersQuery : IRequest<IReadOnlyList<UserDailyUsageDto>>;

public sealed class GetUsersHandler : IRequestHandler<GetUsersQuery, IReadOnlyList<UserDailyUsageDto>>
{
    private readonly PoTrafficDbContext _db;

    public GetUsersHandler(PoTrafficDbContext db) => _db = db;

    public async Task<IReadOnlyList<UserDailyUsageDto>> Handle(GetUsersQuery query, CancellationToken ct)
    {
        DateTimeOffset dayStart = DateTimeOffset.UtcNow.Date;
        DateTimeOffset dayEnd = dayStart.AddDays(1);

        // Load all users with their today's poll records
        List<User> users = await _db.Users
            .AsNoTracking()
            .Include(u => u.Routes)
                .ThenInclude(r => r.PollRecords.Where(p => p.PolledAt >= dayStart && p.PolledAt < dayEnd))
            .ToListAsync(ct);

        // Load cost rates from configuration
        List<SystemConfiguration> configs = await _db.SystemConfigurations
            .Where(c => c.Key.StartsWith("cost.perpoll."))
            .ToListAsync(ct);

        double googleCost = GetCost(configs, "cost.perpoll.googlemaps");
        double tomtomCost = GetCost(configs, "cost.perpoll.tomtom");

        return users.Select(u =>
        {
            IEnumerable<PollRecord> todayPolls = u.Routes.SelectMany(r => r.PollRecords);
            int totalCount = todayPolls.Count();

            // Build provider breakdown per route provider
            var breakdown = u.Routes
                .GroupBy(r => (RouteProvider)r.Provider)
                .Select(grp =>
                {
                    int pollCount = grp.SelectMany(r => r.PollRecords).Count();
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
