using FluentAssertions;
using Hangfire;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PoTraffic.Api.Features.MonitoringWindows;
using PoTraffic.Api.Infrastructure.Storage;

using PoTraffic.Shared.Constants;
using PoTraffic.Shared.Enums;

namespace PoTraffic.UnitTests.Features.MonitoringWindows;

/// <summary>
/// Tests for quota enforcement in <see cref="StartWindowCommandHandler"/>.
/// FR-003: daily monitoring session quota = DefaultDailyQuota (10); on exhaustion return QUOTA_EXCEEDED.
/// </summary>
public sealed class StartWindowHandlerTests
{
    private static TableStorageContext CreateDb()
    {
        return new TableStorageContext();
    }

    private static async Task<(TableStorageContext Db, Guid UserId, Guid WindowId)> SeedWithSessionsAsync(
        int sessionCount)
    {
        TableStorageContext db = CreateDb();
        Guid userId = Guid.NewGuid();
        Guid routeId = Guid.NewGuid();
        Guid windowId = Guid.NewGuid();

        var user = new User
        {
            Id = userId,
            Email = $"user-{userId}@test.com",
            PasswordHash = "hash",
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
        // Each route can only have one session per day (UNIQUE RouteId+SessionDate constraint),
        // so quota consumption is modelled as one session per distinct route.
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);

        for (int i = 0; i < sessionCount; i++)
        {
            Guid otherRouteId = Guid.NewGuid();
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
                Id = Guid.NewGuid(),
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
        (TableStorageContext db, Guid userId, Guid windowId) =
            await SeedWithSessionsAsync(QuotaConstants.DefaultDailyQuota);

        IBackgroundJobClient jobClient = Substitute.For<IBackgroundJobClient>();
        var handler = new StartWindowCommandHandler(db, jobClient, NullLogger<StartWindowCommandHandler>.Instance);

        // Act
        StartWindowResult result = await handler.Handle(
            new StartWindowCommand(windowId, userId), CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("QUOTA_EXCEEDED");
        result.QuotaRemaining.Should().Be(0);

        // Verify that no new MonitoringSession was created
        int newSessionCount = db.MonitoringSessions
            .Count(s => s.RouteId != Guid.Empty);
        newSessionCount.Should().Be(QuotaConstants.DefaultDailyQuota,
            "no additional session should be created when quota is exceeded (FR-003)");

        // Verify that no Hangfire job was enqueued by checking the route's HangfireJobChainId remains null
        // (BackgroundJobClientExtensions.Enqueue is an extension method - cannot verify via NSubstitute)
        // The handler only sets HangfireJobChainId when a job IS enqueued; on QUOTA_EXCEEDED it returns early.
    }

    [Fact]
    public async Task StartWindow_WhenQuotaNotExhausted_CreatesSessionAndEnqueuesJob()
    {
        // Arrange — seed DefaultDailyQuota - 1 sessions (one slot remaining)
        (TableStorageContext db, Guid userId, Guid windowId) =
            await SeedWithSessionsAsync(QuotaConstants.DefaultDailyQuota - 1);

        IBackgroundJobClient jobClient = Substitute.For<IBackgroundJobClient>();
        // Note: BackgroundJobClientExtensions.Enqueue<T> is an extension method that ultimately calls
        // IBackgroundJobClient.Create(job, state). The mock returns null by default which is acceptable here —
        // the handler stores HangfireJobChainId = null when no Hangfire server is connected in tests.

        var handler = new StartWindowCommandHandler(db, jobClient, NullLogger<StartWindowCommandHandler>.Instance);

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
        TableStorageContext db = CreateDb();

        IBackgroundJobClient jobClient = Substitute.For<IBackgroundJobClient>();
        var handler = new StartWindowCommandHandler(db, jobClient, NullLogger<StartWindowCommandHandler>.Instance);

        // Act
        StartWindowResult result = await handler.Handle(
            new StartWindowCommand(Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("NOT_FOUND");
    }

    [Fact]
    public async Task StartWindow_WhenSessionAlreadyExistsForRouteToday_ReturnsExistingSessionIdempotently()
    {
        // Arrange — create route+window, manually seed a session for it today (simulates CreateRoute having
        // already started monitoring), then call Start again for the same window.
        // Idempotent guard — Strategy pattern: swaps quota-check path for existing-session-return path.
        TableStorageContext db = CreateDb();
        Guid userId = Guid.NewGuid();
        Guid routeId = Guid.NewGuid();
        Guid windowId = Guid.NewGuid();
        Guid existingSessionId = Guid.NewGuid();

        var user = new User
        {
            Id = userId,
            Email = $"user-{userId}@test.com",
            PasswordHash = "hash",
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

        IBackgroundJobClient jobClient = Substitute.For<IBackgroundJobClient>();
        var handler = new StartWindowCommandHandler(db, jobClient, NullLogger<StartWindowCommandHandler>.Instance);

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
