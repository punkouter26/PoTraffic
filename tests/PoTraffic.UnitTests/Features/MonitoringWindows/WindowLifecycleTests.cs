using FluentAssertions;
using Hangfire;
using Hangfire.States;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PoTraffic.Api.Features.MonitoringWindows;
using PoTraffic.Api.Infrastructure.Data;

using PoTraffic.Shared.Enums;

namespace PoTraffic.UnitTests.Features.MonitoringWindows;

/// <summary>
/// Tests for <see cref="StopWindowCommandHandler"/> lifecycle transitions.
/// Verifies session transitions to Completed and Hangfire job chain is cancelled.
/// </summary>
public sealed class WindowLifecycleTests
{
    private static PoTrafficDbContext CreateDb(string name)
    {
        DbContextOptions<PoTrafficDbContext> opts = new DbContextOptionsBuilder<PoTrafficDbContext>()
            .UseInMemoryDatabase(name)
            .Options;
        return new PoTrafficDbContext(opts);
    }

    private static async Task<(PoTrafficDbContext Db, Guid SessionId, Guid RouteId, Guid UserId)> SeedActiveSessionAsync(
        string dbName,
        string? hangfireJobChainId = "job-abc-123")
    {
        PoTrafficDbContext db = CreateDb(dbName);
        Guid userId = Guid.NewGuid();
        Guid routeId = Guid.NewGuid();
        Guid sessionId = Guid.NewGuid();

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
            HangfireJobChainId = hangfireJobChainId,
            CreatedAt = DateTimeOffset.UtcNow
        });

        db.MonitoringSessions.Add(new MonitoringSession
        {
            Id = sessionId,
            RouteId = routeId,
            SessionDate = DateOnly.FromDateTime(DateTime.UtcNow),
            State = (int)SessionState.Active
        });

        await db.SaveChangesAsync();
        return (db, sessionId, routeId, userId);
    }

    [Fact]
    public async Task StopWindow_TransitionsSessionToCompleted()
    {
        // Arrange
        string dbName = Guid.NewGuid().ToString();
        (PoTrafficDbContext db, Guid sessionId, _, Guid userId) =
            await SeedActiveSessionAsync(dbName, "hangfire-job-1");

        IBackgroundJobClient jobClient = Substitute.For<IBackgroundJobClient>();
        var handler = new StopWindowCommandHandler(db, jobClient, NullLogger<StopWindowCommandHandler>.Instance);

        // Act
        bool result = await handler.Handle(new StopWindowCommand(sessionId, userId), CancellationToken.None);

        // Assert
        result.Should().BeTrue();

        MonitoringSession? session = await db.MonitoringSessions.FindAsync(sessionId);
        session.Should().NotBeNull();
        session!.State.Should().Be((int)SessionState.Completed,
            "StopWindowCommand must transition session to Completed state");
    }

    [Fact]
    public async Task StopWindow_DeletesHangfireJobChain()
    {
        // Arrange
        const string jobId = "hangfire-job-42";
        string dbName = Guid.NewGuid().ToString();
        (PoTrafficDbContext db, Guid sessionId, _, Guid userId) =
            await SeedActiveSessionAsync(dbName, jobId);

        IBackgroundJobClient jobClient = Substitute.For<IBackgroundJobClient>();
        // BackgroundJobClientExtensions.Delete is an extension method that calls ChangeState internally.
        // We verify the underlying ChangeState was invoked (NSubstitute cannot intercept extension methods directly).
        jobClient.ChangeState(jobId, Arg.Any<Hangfire.States.IState>(), Arg.Any<string>()).Returns(true);

        var handler = new StopWindowCommandHandler(db, jobClient, NullLogger<StopWindowCommandHandler>.Instance);

        // Act
        bool result = await handler.Handle(new StopWindowCommand(sessionId, userId), CancellationToken.None);

        // Assert
        result.Should().BeTrue();
        jobClient.Received(1).ChangeState(jobId, Arg.Any<Hangfire.States.IState>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task StopWindow_ClearsHangfireJobChainIdOnRoute()
    {
        // Arrange
        const string jobId = "hangfire-job-99";
        string dbName = Guid.NewGuid().ToString();
        (PoTrafficDbContext db, Guid sessionId, Guid routeId, Guid userId) =
            await SeedActiveSessionAsync(dbName, jobId);

        IBackgroundJobClient jobClient = Substitute.For<IBackgroundJobClient>();
        var handler = new StopWindowCommandHandler(db, jobClient, NullLogger<StopWindowCommandHandler>.Instance);

        // Act
        await handler.Handle(new StopWindowCommand(sessionId, userId), CancellationToken.None);

        // Assert — HangfireJobChainId should be nulled out
        Route? route = await db.Routes.FindAsync(routeId);
        route.Should().NotBeNull();
        route!.HangfireJobChainId.Should().BeNull(
            "after stopping monitoring, HangfireJobChainId should be cleared to prevent orphaned chains");
    }

    [Fact]
    public async Task StopWindow_WhenSessionNotFound_ReturnsFalse()
    {
        // Arrange
        string dbName = Guid.NewGuid().ToString();
        PoTrafficDbContext db = CreateDb(dbName);

        IBackgroundJobClient jobClient = Substitute.For<IBackgroundJobClient>();
        var handler = new StopWindowCommandHandler(db, jobClient, NullLogger<StopWindowCommandHandler>.Instance);

        // Act
        bool result = await handler.Handle(
            new StopWindowCommand(Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None);

        // Assert
        result.Should().BeFalse();
        // BackgroundJobClientExtensions.Delete calls ChangeState — verify it was NOT called
        jobClient.DidNotReceive().ChangeState(Arg.Any<string>(), Arg.Any<Hangfire.States.IState>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task StopWindow_WhenNoHangfireJobId_DoesNotCallDelete()
    {
        // Arrange — route has no HangfireJobChainId
        string dbName = Guid.NewGuid().ToString();
        (PoTrafficDbContext db, Guid sessionId, _, Guid userId) =
            await SeedActiveSessionAsync(dbName, hangfireJobChainId: null);

        IBackgroundJobClient jobClient = Substitute.For<IBackgroundJobClient>();
        var handler = new StopWindowCommandHandler(db, jobClient, NullLogger<StopWindowCommandHandler>.Instance);

        // Act
        bool result = await handler.Handle(new StopWindowCommand(sessionId, userId), CancellationToken.None);

        // Assert
        result.Should().BeTrue("session with no job chain should still stop successfully");
        // BackgroundJobClientExtensions.Delete calls ChangeState — verify it was NOT called
        jobClient.DidNotReceive().ChangeState(Arg.Any<string>(), Arg.Any<Hangfire.States.IState>(), Arg.Any<string?>());
    }
}
