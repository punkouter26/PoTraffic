using FluentAssertions;
using Hangfire;
using Hangfire.States;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PoTraffic.Api.Features.MonitoringWindows;
using PoTraffic.Api.Infrastructure.Storage;

using PoTraffic.Shared.Enums;

namespace PoTraffic.UnitTests.Features.MonitoringWindows;

/// <summary>
/// Tests for <see cref="StopWindowCommandHandler"/> lifecycle transitions.
/// Verifies session transitions to Completed and Hangfire job chain is cancelled.
/// </summary>
public sealed class WindowLifecycleTests
{
    private static TableStorageContext CreateDb()
    {
        return new TableStorageContext();
    }

    private static async Task<(TableStorageContext Db, Guid SessionId, Guid RouteId, Guid UserId)> SeedActiveSessionAsync(
        string? hangfireJobChainId = "job-abc-123")
    {
        TableStorageContext db = CreateDb();
        Guid userId = Guid.NewGuid();
        Guid routeId = Guid.NewGuid();
        Guid sessionId = Guid.NewGuid();

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
            HangfireJobChainId = hangfireJobChainId,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.Add(route);

        db.Add(new MonitoringSession
        {
            Id = sessionId,
            RouteId = routeId,
            Route = route,
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
        (TableStorageContext db, Guid sessionId, _, Guid userId) =
            await SeedActiveSessionAsync("hangfire-job-1");

        IBackgroundJobClient jobClient = Substitute.For<IBackgroundJobClient>();
        var handler = new StopWindowCommandHandler(db, jobClient, NullLogger<StopWindowCommandHandler>.Instance);

        // Act
        bool result = await handler.Handle(new StopWindowCommand(sessionId, userId), CancellationToken.None);

        // Assert
        result.Should().BeTrue();

        MonitoringSession? session = db.MonitoringSessions.FirstOrDefault(x => x.Id == sessionId);
        session.Should().NotBeNull();
        session!.State.Should().Be((int)SessionState.Completed,
            "StopWindowCommand must transition session to Completed state");
    }

    [Fact]
    public async Task StopWindow_DeletesHangfireJobChain()
    {
        // Arrange
        const string jobId = "hangfire-job-42";
        (TableStorageContext db, Guid sessionId, _, Guid userId) =
            await SeedActiveSessionAsync(jobId);

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
        (TableStorageContext db, Guid sessionId, Guid routeId, Guid userId) =
            await SeedActiveSessionAsync(jobId);

        IBackgroundJobClient jobClient = Substitute.For<IBackgroundJobClient>();
        var handler = new StopWindowCommandHandler(db, jobClient, NullLogger<StopWindowCommandHandler>.Instance);

        // Act
        await handler.Handle(new StopWindowCommand(sessionId, userId), CancellationToken.None);

        // Assert — HangfireJobChainId should be nulled out
        Route? route = db.Routes.FirstOrDefault(x => x.Id == routeId);
        route.Should().NotBeNull();
        route!.HangfireJobChainId.Should().BeNull(
            "after stopping monitoring, HangfireJobChainId should be cleared to prevent orphaned chains");
    }

    [Fact]
    public async Task StopWindow_WhenSessionNotFound_ReturnsFalse()
    {
        // Arrange
        TableStorageContext db = CreateDb();

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
        (TableStorageContext db, Guid sessionId, _, Guid userId) =
            await SeedActiveSessionAsync(hangfireJobChainId: null);

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
