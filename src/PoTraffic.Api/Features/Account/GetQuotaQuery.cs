using MediatR;
using PoTraffic.Api.Infrastructure.Storage;


using PoTraffic.Shared.DTOs.Account;

namespace PoTraffic.Api.Features.Account;

public sealed record GetQuotaQuery(Guid UserId) : IRequest<QuotaDto?>;

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

        int usedToday = _db.MonitoringSessions
            .Count(s =>
                s.Route.UserId == query.UserId &&
                s.SessionDate >= DateOnly.FromDateTime(dayStart.UtcDateTime) &&
                s.SessionDate <= DateOnly.FromDateTime(dayEnd.UtcDateTime));

        int remaining = Math.Max(0, dailyLimit - usedToday);

        // Reset time = midnight UTC next day
        DateTimeOffset resetsAt = dayEnd;

        return new QuotaDto(
            DailyLimit: dailyLimit,
            UsedToday: usedToday,
            Remaining: remaining,
            ResetsAtUtc: resetsAt);
    }
}
