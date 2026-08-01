using FluentAssertions;
using System.Linq.Expressions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PoTraffic.API.Features.MonitoringWindows;
using PoTraffic.API.Infrastructure.Storage;
using PoTraffic.API.Infrastructure.Scheduling;

using PoTraffic.Shared.Constants;
using PoTraffic.Shared.Enums;
using PoTraffic.UnitTests.Helpers;

namespace PoTraffic.UnitTests.Features.MonitoringWindows;

/// <summary>
/// Tests for quota enforcement in <see cref="StartWindowCommandHandler"/>.
/// FR-003: daily monitoring session quota = DefaultDailyQuota (10); on exhaustion return QUOTA_EXCEEDED.
/// </summary>
public sealed class StartWindowHandlerTests
{

    private static async Task<(TableStorageContext Db, UserId UserId, WindowId WindowId)> SeedWithSessionsAsync(
        int sessionCount)
    {
        TableStorageContext db = TestDoubles.CreateDb();
        UserId userId = UserId.New();
        RouteId routeId = RouteId.New();
        WindowId windowId = WindowId.New();

        var user = new User
        {
            Id = userId,
            Email = $"user-{userId}@test.com",
            Locale = "Europe/London"
        };
        db.Add(user);

        // The target route that the test will attempt to start
        var route = new Route
        {
            Id = routeId,
            UserId = userId,
            User = user,
            OriginAddress = "A",
            OriginCoordinates = "1.0,1.0",
            DestinationAddress = "B",
            DestinationCoordinates = "2.0,2.0",
            Provider = (int)RouteProvider.GoogleMaps,
            MonitoringStatus = (int)MonitoringStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.Add(route);

        var window = new MonitoringWindow
        {
            Id = windowId,
            RouteId = routeId,
            Route = route,
            StartTime = new TimeOnly(7, 0),
            EndTime = new TimeOnly(9, 0),
            DaysOfWeekMask = 0b01111110 // Mon-Fri
        };
        db.Add(window);

        // Seed today's sessions on DIFFERENT routes for the same user.
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);

        for (int i = 0; i < sessionCount; i++)
        {
            RouteId otherRouteId = RouteId.New();
            var otherRoute = new Route
            {
                Id = otherRouteId,
                UserId = userId,
                User = user,
                OriginAddress = $"X{i}",
                OriginCoordinates = $"{i}.0,{i}.0",
                DestinationAddress = $"Y{i}",
                DestinationCoordinates = $"{i + 1}.0,{i + 1}.0",
                Provider = (int)RouteProvider.GoogleMaps,
                MonitoringStatus = (int)MonitoringStatus.Active,
                CreatedAt = DateTimeOffset.UtcNow
            };
            db.Add(otherRoute);
            db.Add(new MonitoringSession
            {
                Id = SessionId.New(),
                RouteId = otherRouteId,
                Route = otherRoute,
                SessionDate = today,
                State = (int)SessionState.Completed
            });
        }

        await db.SaveChangesAsync();
        return (db, userId, windowId);
    }

    [Fact]
    public async Task StartWindow_WhenQuotaExhausted_ReturnsQuotaExceededError()
    {
        // Arrange — seed exactly DefaultDailyQuota sessions for today
        (TableStorageContext db, UserId userId, WindowId windowId) =
            await SeedWithSessionsAsync(QuotaConstants.DefaultDailyQuota);

        IJobScheduler scheduler = Substitute.For<IJobScheduler>();
        var handler = new StartWindowCommandHandler(db, scheduler, NullLogger<StartWindowCommandHandler>.Instance);

        // Act
        StartWindowResult result = await handler.Handle(
            new StartWindowCommand(windowId, userId), CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("QUOTA_EXCEEDED");
        result.QuotaRemaining.Should().Be(0);

        // Verify that no new MonitoringSession was created
        int newSessionCount = db.MonitoringSessions
            .Count(s => !s.RouteId.IsEmpty);
        newSessionCount.Should().Be(QuotaConstants.DefaultDailyQuota,
            "no additional session should be created when quota is exceeded (FR-003)");
    }

    [Fact]
    public async Task StartWindow_WhenQuotaNotExhausted_CreatesSessionAndEnqueuesJob()
    {
        // Arrange — seed DefaultDailyQuota - 1 sessions (one slot remaining)
        (TableStorageContext db, UserId userId, WindowId windowId) =
            await SeedWithSessionsAsync(QuotaConstants.DefaultDailyQuota - 1);

        IJobScheduler scheduler = Substitute.For<IJobScheduler>();
        scheduler.Enqueue(Arg.Any<Expression<Func<Task>>>()).Returns("fake-job-id");

        var handler = new StartWindowCommandHandler(db, scheduler, NullLogger<StartWindowCommandHandler>.Instance);

        // Act
        StartWindowResult result = await handler.Handle(
            new StartWindowCommand(windowId, userId), CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.ErrorCode.Should().BeNull();
        result.QuotaRemaining.Should().Be(0, "the last remaining quota slot was consumed");
        result.SessionId.Should().NotBeNull();
    }

    [Fact]
    public async Task StartWindow_WhenWindowNotFound_ReturnsNotFoundError()
    {
        // Arrange
        TableStorageContext db = TestDoubles.CreateDb();

        IJobScheduler scheduler = Substitute.For<IJobScheduler>();
        var handler = new StartWindowCommandHandler(db, scheduler, NullLogger<StartWindowCommandHandler>.Instance);

        // Act
        StartWindowResult result = await handler.Handle(
            new StartWindowCommand(WindowId.New(), UserId.New()), CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("NOT_FOUND");
    }

    [Fact]
    public async Task StartWindow_WhenSessionAlreadyExistsForRouteToday_ReturnsExistingSessionIdempotently()
    {
        // Arrange — create route+window, manually seed a session for it today
        TableStorageContext db = TestDoubles.CreateDb();
        UserId userId = UserId.New();
        RouteId routeId = RouteId.New();
        WindowId windowId = WindowId.New();
        SessionId existingSessionId = SessionId.New();

        var user = new User
        {
            Id = userId,
            Email = $"user-{userId}@test.com",
            Locale = "Europe/London"
        };
        db.Add(user);
        var route = new Route
        {
            Id = routeId,
            UserId = userId,
            User = user,
            OriginAddress = "A",
            OriginCoordinates = "1.0,1.0",
            DestinationAddress = "B",
            DestinationCoordinates = "2.0,2.0",
            Provider = (int)RouteProvider.GoogleMaps,
            MonitoringStatus = (int)MonitoringStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.Add(route);
        db.Add(new MonitoringWindow
        {
            Id = windowId,
            RouteId = routeId,
            Route = route,
            StartTime = new TimeOnly(7, 0),
            EndTime = new TimeOnly(9, 0),
            DaysOfWeekMask = 0b01111110
        });
        db.Add(new MonitoringSession
        {
            Id = existingSessionId,
            RouteId = routeId,
            Route = route,
            SessionDate = DateOnly.FromDateTime(DateTime.UtcNow),
            State = (int)SessionState.Active
        });
        await db.SaveChangesAsync();

        IJobScheduler scheduler = Substitute.For<IJobScheduler>();
        var handler = new StartWindowCommandHandler(db, scheduler, NullLogger<StartWindowCommandHandler>.Instance);

        // Act — second call for same route, same day
        StartWindowResult result = await handler.Handle(
            new StartWindowCommand(windowId, userId), CancellationToken.None);

        // Assert — idempotent: succeed and return the EXISTING session, no new session created
        result.IsSuccess.Should().BeTrue("duplicate start requests should be idempotent (FR-004)");
        result.ErrorCode.Should().BeNull();
        result.SessionId.Should().Be(existingSessionId);
        int totalSessions = db.MonitoringSessions.Count();
        totalSessions.Should().Be(1, "no duplicate session should be inserted");
    }
}
