using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PoTraffic.Api.Features.MonitoringWindows;
using PoTraffic.Api.Infrastructure.Storage;
using PoTraffic.Api.Infrastructure.Scheduling;

using PoTraffic.Shared.Enums;

namespace PoTraffic.Tests.Features.MonitoringWindows;

/// <summary>
/// Tests for <see cref="StopWindowCommandHandler"/> lifecycle transitions.
/// Verifies session transitions to Completed and job chain is cancelled.
/// </summary>
public sealed class WindowLifecycleTests
{
    private static TableStorageContext CreateDb()
    {
        return new TableStorageContext();
    }

    private static async Task<(TableStorageContext Db, Guid SessionId, Guid RouteId, Guid UserId)> SeedActiveSessionAsync(
        string? jobChainId = "job-abc-123")
    {
        TableStorageContext db = CreateDb();
        Guid userId = Guid.NewGuid();
        Guid routeId = Guid.NewGuid();
        Guid sessionId = Guid.NewGuid();

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
            JobChainId = jobChainId,
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
            await SeedActiveSessionAsync("job-1");

        IJobScheduler scheduler = Substitute.For<IJobScheduler>();
        var handler = new StopWindowCommandHandler(db, scheduler, NullLogger<StopWindowCommandHandler>.Instance);

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
    public async Task StopWindow_DeletesJobChain()
    {
        // Arrange
        const string jobId = "job-42";
        (TableStorageContext db, Guid sessionId, _, Guid userId) =
            await SeedActiveSessionAsync(jobId);

        IJobScheduler scheduler = Substitute.For<IJobScheduler>();

        var handler = new StopWindowCommandHandler(db, scheduler, NullLogger<StopWindowCommandHandler>.Instance);

        // Act
        bool result = await handler.Handle(new StopWindowCommand(sessionId, userId), CancellationToken.None);

        // Assert
        result.Should().BeTrue();
        scheduler.Received(1).Cancel(jobId);
    }

    [Fact]
    public async Task StopWindow_ClearsJobChainIdOnRoute()
    {
        // Arrange
        const string jobId = "job-99";
        (TableStorageContext db, Guid sessionId, Guid routeId, Guid userId) =
            await SeedActiveSessionAsync(jobId);

        IJobScheduler scheduler = Substitute.For<IJobScheduler>();
        var handler = new StopWindowCommandHandler(db, scheduler, NullLogger<StopWindowCommandHandler>.Instance);

        // Act
        await handler.Handle(new StopWindowCommand(sessionId, userId), CancellationToken.None);

        // Assert — JobChainId should be nulled out
        Route? route = db.Routes.FirstOrDefault(x => x.Id == routeId);
        route.Should().NotBeNull();
        route!.JobChainId.Should().BeNull(
            "after stopping monitoring, JobChainId should be cleared to prevent orphaned chains");
    }

    [Fact]
    public async Task StopWindow_WhenSessionNotFound_ReturnsFalse()
    {
        // Arrange
        TableStorageContext db = CreateDb();

        IJobScheduler scheduler = Substitute.For<IJobScheduler>();
        var handler = new StopWindowCommandHandler(db, scheduler, NullLogger<StopWindowCommandHandler>.Instance);

        // Act
        bool result = await handler.Handle(
            new StopWindowCommand(Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None);

        // Assert
        result.Should().BeFalse();
        scheduler.DidNotReceive().Cancel(Arg.Any<string>());
    }

    [Fact]
    public async Task StopWindow_WhenNoJobId_DoesNotCallCancel()
    {
        // Arrange — route has no JobChainId
        (TableStorageContext db, Guid sessionId, _, Guid userId) =
            await SeedActiveSessionAsync(jobChainId: null);

        IJobScheduler scheduler = Substitute.For<IJobScheduler>();
        var handler = new StopWindowCommandHandler(db, scheduler, NullLogger<StopWindowCommandHandler>.Instance);

        // Act
        bool result = await handler.Handle(new StopWindowCommand(sessionId, userId), CancellationToken.None);

        // Assert
        result.Should().BeTrue("session with no job chain should still stop successfully");
        scheduler.DidNotReceive().Cancel(Arg.Any<string>());
    }
}
