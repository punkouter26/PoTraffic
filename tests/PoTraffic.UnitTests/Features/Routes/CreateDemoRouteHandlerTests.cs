using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PoTraffic.API.Infrastructure.Storage;
using PoTraffic.Shared.DTOs.Routes;
using PoTraffic.Shared.Enums;
using PoTraffic.UnitTests.Helpers;

namespace PoTraffic.UnitTests.Features.Routes;

/// <summary>
/// Tests for <see cref="CreateDemoRouteCommandHandler"/> (#10) — the sample route an empty
/// account can load to see the product working before its own data exists.
///
/// <para>
/// The load-bearing guarantees are that it costs nothing (no provider call, no monitoring
/// window, no session) and that it cannot be mistaken for measurement.
/// </para>
/// </summary>
public sealed class CreateDemoRouteHandlerTests
{
    private static CreateDemoRouteCommandHandler HandlerFor(TableStorageContext db) =>
        new(db, NullLogger<CreateDemoRouteCommandHandler>.Instance);

    [Fact]
    public async Task CreateDemoRoute_CreatesPausedFlaggedRouteWithSyntheticHistory()
    {
        TableStorageContext db = TestDoubles.CreateDb();
        UserId userId = UserId.New();

        CreateRouteResult result = await HandlerFor(db).Handle(
            new CreateDemoRouteCommand(userId), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Route!.IsDemo.Should().BeTrue("the UI labels the route from this flag");
        result.Route.MonitoringStatus.Should().Be(MonitoringStatus.Paused);

        EntityRoute stored = db.Routes.Single(r => r.UserId == userId);
        stored.IsDemo.Should().BeTrue();
        db.PollRecords.Count(p => p.RouteId == stored.Id)
            .Should().BeGreaterThan(0, "the point of the demo is that the charts have something to draw");
    }

    [Fact]
    public async Task CreateDemoRoute_CreatesNothingThatCouldBillAProvider()
    {
        TableStorageContext db = TestDoubles.CreateDb();
        UserId userId = UserId.New();

        CreateRouteResult result = await HandlerFor(db).Handle(
            new CreateDemoRouteCommand(userId), CancellationToken.None);

        RouteId routeId = result.Route!.Id;

        // No monitoring window means PollRouteJob has nothing to arm a polling chain from,
        // and no session means the daily quota is untouched.
        db.MonitoringWindows.Should().NotContain(w => w.RouteId == routeId);
        db.MonitoringSessions.Should().NotContain(s => s.RouteId == routeId);
        db.PollRecords.Where(p => p.RouteId == routeId)
            .Should().OnlyContain(p => p.SessionId == null);
    }

    [Fact]
    public async Task CreateDemoRoute_CalledTwice_ReturnsTheSameRouteWithoutDuplicatingHistory()
    {
        TableStorageContext db = TestDoubles.CreateDb();
        UserId userId = UserId.New();

        CreateRouteResult first = await HandlerFor(db).Handle(
            new CreateDemoRouteCommand(userId), CancellationToken.None);
        int pollsAfterFirst = db.PollRecords.Count();

        CreateRouteResult second = await HandlerFor(db).Handle(
            new CreateDemoRouteCommand(userId), CancellationToken.None);

        second.IsSuccess.Should().BeTrue();
        second.Route!.Id.Should().Be(first.Route!.Id, "a second click must not stack up sample routes");
        db.Routes.Count(r => r.UserId == userId).Should().Be(1);
        db.PollRecords.Count().Should().Be(pollsAfterFirst);
    }

    [Fact]
    public async Task CreateDemoRoute_AfterTheDemoWasDeleted_CreatesAFreshOne()
    {
        TableStorageContext db = TestDoubles.CreateDb();
        UserId userId = UserId.New();

        CreateRouteResult first = await HandlerFor(db).Handle(
            new CreateDemoRouteCommand(userId), CancellationToken.None);

        EntityRoute stored = db.Routes.Single(r => r.Id == first.Route!.Id);
        stored.MonitoringStatus = (int)MonitoringStatus.Deleted;
        await db.SaveChangesAsync();

        CreateRouteResult second = await HandlerFor(db).Handle(
            new CreateDemoRouteCommand(userId), CancellationToken.None);

        second.Route!.Id.Should().NotBe(first.Route!.Id,
            "a user who deleted the sample route and asks again should get one back");
    }

    [Fact]
    public async Task CreateDemoRoute_DoesNotTouchAnotherUsersDemoRoute()
    {
        TableStorageContext db = TestDoubles.CreateDb();
        UserId first = UserId.New();
        UserId second = UserId.New();

        CreateRouteResult a = await HandlerFor(db).Handle(new CreateDemoRouteCommand(first), CancellationToken.None);
        CreateRouteResult b = await HandlerFor(db).Handle(new CreateDemoRouteCommand(second), CancellationToken.None);

        b.Route!.Id.Should().NotBe(a.Route!.Id, "the idempotency guard is per-user, not global");
        db.Routes.Count().Should().Be(2);
    }

    [Fact]
    public async Task CreateDemoRoute_SeedsWeekdayOnlySamplesThatNeverRunIntoTheFuture()
    {
        TableStorageContext db = TestDoubles.CreateDb();

        await HandlerFor(db).Handle(new CreateDemoRouteCommand(UserId.New()), CancellationToken.None);

        List<PollRecord> history = [.. db.PollRecords];

        history.Should().NotBeEmpty();
        history.Should().OnlyContain(p => p.PolledAt <= DateTimeOffset.UtcNow,
            "history that runs into the future would be visibly wrong on the past-24h views");
        history.Select(p => p.PolledAt.DayOfWeek)
            .Should().NotContain([DayOfWeek.Saturday, DayOfWeek.Sunday],
                "the sample is a weekday commute");
    }

    [Fact]
    public async Task CreateDemoRoute_MakesRushHourSlowerThanTheShoulder()
    {
        TableStorageContext db = TestDoubles.CreateDb();

        await HandlerFor(db).Handle(new CreateDemoRouteCommand(UserId.New()), CancellationToken.None);

        double peak = db.PollRecords.Where(p => p.PolledAt.Hour == 8).Average(p => p.TravelDurationSeconds);
        double shoulder = db.PollRecords.Where(p => p.PolledAt.Hour == 6).Average(p => p.TravelDurationSeconds);

        peak.Should().BeGreaterThan(shoulder * 1.2,
            "a flat series would render an empty-looking heatmap and a meaningless baseline");
    }

    [Fact]
    public async Task CreateDemoRoute_ProducesTheSameSampleDataForEveryAccount()
    {
        TableStorageContext first = TestDoubles.CreateDb();
        TableStorageContext second = TestDoubles.CreateDb();

        await HandlerFor(first).Handle(new CreateDemoRouteCommand(UserId.New()), CancellationToken.None);
        await HandlerFor(second).Handle(new CreateDemoRouteCommand(UserId.New()), CancellationToken.None);

        second.PollRecords.Select(p => p.TravelDurationSeconds)
            .Should().Equal(first.PollRecords.Select(p => p.TravelDurationSeconds),
                "a fixed seed keeps every account's sample data — and every screenshot — identical");
    }
}
