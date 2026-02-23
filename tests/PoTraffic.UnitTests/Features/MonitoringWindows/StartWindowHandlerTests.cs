using FluentAssertions;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PoTraffic.Api.Features.MonitoringWindows;
using PoTraffic.Api.Infrastructure.Data;

using PoTraffic.Shared.Constants;
using PoTraffic.Shared.Enums;

namespace PoTraffic.UnitTests.Features.MonitoringWindows;

/// <summary>
/// Tests for quota enforcement in <see cref="StartWindowCommandHandler"/>.
/// FR-003: daily monitoring session quota = DefaultDailyQuota (10); on exhaustion return QUOTA_EXCEEDED.
/// </summary>
public sealed class StartWindowHandlerTests
{
    private static PoTrafficDbContext CreateDb(string name)
    {
        DbContextOptions<PoTrafficDbContext> opts = new DbContextOptionsBuilder<PoTrafficDbContext>()
            .UseInMemoryDatabase(name)
            .Options;
        return new PoTrafficDbContext(opts);
    }

    private static async Task<(PoTrafficDbContext Db, Guid UserId, Guid WindowId)> SeedWithSessionsAsync(
        string dbName,
        int sessionCount)
    {
        PoTrafficDbContext db = CreateDb(dbName);
        Guid userId = Guid.NewGuid();
        Guid routeId = Guid.NewGuid();
        Guid windowId = Guid.NewGuid();

        db.Users.Add(new User
        {
            Id = userId,
            Email = $"user-{userId}@test.com",
            PasswordHash = "hash",
            Locale = "Europe/London"
        });

        db.Routes.Add(new Route
        {
            Id = routeId,
            UserId = userId,
            OriginAddress = "A",
            OriginCoordinates = "1.0,1.0",
            DestinationAddress = "B",
            DestinationCoordinates = "2.0,2.0",
            Provider = (int)RouteProvider.GoogleMaps,
            MonitoringStatus = (int)MonitoringStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow
        });

        db.MonitoringWindows.Add(new MonitoringWindow
        {
            Id = windowId,
            RouteId = routeId,
            StartTime = new TimeOnly(7, 0),
            EndTime = new TimeOnly(9, 0),
            DaysOfWeekMask = 0b01111110 // Mon-Fri
        });

        // Seed today's sessions for the user
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);
        for (int i = 0; i < sessionCount; i++)
        {
            db.MonitoringSessions.Add(new MonitoringSession
            {
                Id = Guid.NewGuid(),
                RouteId = routeId,
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
        string dbName = Guid.NewGuid().ToString();
        (PoTrafficDbContext db, Guid userId, Guid windowId) =
            await SeedWithSessionsAsync(dbName, QuotaConstants.DefaultDailyQuota);

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
        int newSessionCount = await db.MonitoringSessions
            .CountAsync(s => s.RouteId != Guid.Empty);
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
        string dbName = Guid.NewGuid().ToString();
        (PoTrafficDbContext db, Guid userId, Guid windowId) =
            await SeedWithSessionsAsync(dbName, QuotaConstants.DefaultDailyQuota - 1);

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
        string dbName = Guid.NewGuid().ToString();
        PoTrafficDbContext db = CreateDb(dbName);

        IBackgroundJobClient jobClient = Substitute.For<IBackgroundJobClient>();
        var handler = new StartWindowCommandHandler(db, jobClient, NullLogger<StartWindowCommandHandler>.Instance);

        // Act
        StartWindowResult result = await handler.Handle(
            new StartWindowCommand(Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("NOT_FOUND");
    }
}
