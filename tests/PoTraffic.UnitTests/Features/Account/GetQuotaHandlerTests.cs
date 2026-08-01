using FluentAssertions;
using PoTraffic.API.Features.Account;
using PoTraffic.API.Infrastructure.Storage;
using PoTraffic.Shared.DTOs.Account;

namespace PoTraffic.UnitTests.Features.Account;

/// <summary>FR-003: /api/account/quota must report the enforced daily session quota.</summary>
public sealed class GetQuotaHandlerTests
{
    private static (TableStorageContext Db, UserId UserId, RouteId RouteId) Seed()
    {
        var db = new TableStorageContext();
        UserId userId = UserId.New();
        RouteId routeId = RouteId.New();
        db.Add(new User { Id = userId, Email = "quota@test.dev", Locale = "en-US", CreatedAt = DateTimeOffset.UtcNow });
        db.Add(new EntityRoute
        {
            Id = routeId,
            UserId = userId,
            OriginAddress = "A",
            OriginCoordinates = "0,0",
            DestinationAddress = "B",
            DestinationCoordinates = "1,1"
        });
        db.SeedDefaultConfigurationsIfMissing();
        return (db, userId, routeId);
    }

    [Fact]
    public async Task Quota_CountsOnlyTodaysSessions_ForOwnRoutes()
    {
        (TableStorageContext db, UserId userId, RouteId routeId) = Seed();
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow.Date);

        db.Add(new PoTraffic.API.Features.MonitoringWindows.MonitoringSession
        { Id = SessionId.New(), RouteId = routeId, SessionDate = today });
        db.Add(new PoTraffic.API.Features.MonitoringWindows.MonitoringSession
        { Id = SessionId.New(), RouteId = routeId, SessionDate = today.AddDays(-1) });
        // Another user's session must not count.
        db.Add(new PoTraffic.API.Features.MonitoringWindows.MonitoringSession
        { Id = SessionId.New(), RouteId = RouteId.New(), SessionDate = today });

        QuotaDto? quota = await new GetQuotaHandler(db).Handle(new GetQuotaQuery(userId), CancellationToken.None);

        quota.Should().NotBeNull();
        quota!.DailyLimit.Should().Be(10, "seeded quota.daily.default is 10");
        quota.UsedToday.Should().Be(1, "only today's sessions on the user's own routes count");
        quota.Remaining.Should().Be(9);
    }

    [Fact]
    public async Task Quota_ForUnknownUser_ReturnsNull()
    {
        (TableStorageContext db, _, _) = Seed();
        QuotaDto? quota = await new GetQuotaHandler(db).Handle(new GetQuotaQuery(UserId.New()), CancellationToken.None);
        quota.Should().BeNull();
    }
}
