using FluentAssertions;
using PoTraffic.Api.Features.Account;
using PoTraffic.Api.Infrastructure.Storage;
using PoTraffic.Shared.DTOs.Account;

namespace PoTraffic.Tests.Features.Account;

/// <summary>FR-003: /api/account/quota must report the enforced daily session quota.</summary>
public sealed class GetQuotaHandlerTests
{
    private static (TableStorageContext Db, Guid UserId, Guid RouteId) Seed()
    {
        var db = new TableStorageContext();
        Guid userId = Guid.NewGuid();
        Guid routeId = Guid.NewGuid();
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
        (TableStorageContext db, Guid userId, Guid routeId) = Seed();
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow.Date);

        db.Add(new PoTraffic.Api.Features.MonitoringWindows.Entities.MonitoringSession
        { Id = Guid.NewGuid(), RouteId = routeId, SessionDate = today });
        db.Add(new PoTraffic.Api.Features.MonitoringWindows.Entities.MonitoringSession
        { Id = Guid.NewGuid(), RouteId = routeId, SessionDate = today.AddDays(-1) });
        // Another user's session must not count.
        db.Add(new PoTraffic.Api.Features.MonitoringWindows.Entities.MonitoringSession
        { Id = Guid.NewGuid(), RouteId = Guid.NewGuid(), SessionDate = today });

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
        QuotaDto? quota = await new GetQuotaHandler(db).Handle(new GetQuotaQuery(Guid.NewGuid()), CancellationToken.None);
        quota.Should().BeNull();
    }
}
