using MediatR;
using Microsoft.EntityFrameworkCore;
using PoTraffic.Api.Infrastructure.Data;
using PoTraffic.Shared.DTOs.Account;

namespace PoTraffic.Api.Features.Account;

public sealed record GetQuotaQuery(Guid UserId) : IRequest<QuotaDto?>;

public sealed class GetQuotaHandler : IRequestHandler<GetQuotaQuery, QuotaDto?>
{
    private readonly PoTrafficDbContext _db;

    public GetQuotaHandler(PoTrafficDbContext db) => _db = db;

    public async Task<QuotaDto?> Handle(GetQuotaQuery query, CancellationToken ct)
    {
        // Check user exists
        bool userExists = await _db.Users.AnyAsync(u => u.Id == query.UserId, ct);
        if (!userExists) return null;

        // Load daily quota limit from SystemConfiguration
        string? limitValue = await _db.SystemConfigurations
            .Where(c => c.Key == "quota.daily.default")
            .Select(c => c.Value)
            .FirstOrDefaultAsync(ct);

        int dailyLimit = int.TryParse(limitValue, out int lim) ? lim : 10;

        // Count today's sessions for this user's routes
        DateTimeOffset dayStart = DateTimeOffset.UtcNow.Date;
        DateTimeOffset dayEnd = dayStart.AddDays(1);

        int usedToday = await _db.MonitoringSessions
            .CountAsync(s =>
                s.Route.UserId == query.UserId &&
                s.SessionDate >= DateOnly.FromDateTime(dayStart.UtcDateTime) &&
                s.SessionDate <= DateOnly.FromDateTime(dayEnd.UtcDateTime), ct);

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
