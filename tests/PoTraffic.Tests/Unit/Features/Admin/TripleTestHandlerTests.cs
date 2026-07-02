using FluentAssertions;
using System.Linq.Expressions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PoTraffic.Api.Features.Admin;
using PoTraffic.Api.Infrastructure.Storage;
using PoTraffic.Api.Infrastructure.Providers;
using PoTraffic.Api.Infrastructure.Scheduling;
using PoTraffic.Shared.Enums;

namespace PoTraffic.Tests.Features.Admin;

/// <summary>
/// Unit tests for <see cref="StartTripleTestCommandHandler"/> and <see cref="GetTripleTestSessionQueryHandler"/>.
/// FR-TT: Triple Test schedules 3 independent shots and returns winner based on shortest duration.
/// </summary>
public sealed class TripleTestHandlerTests
{
    private static TableStorageContext CreateDb()
    {
        return new TableStorageContext();
    }

    private static ITrafficProviderFactory BuildProviderFactory(ITrafficProvider provider)
    {
        var factory = Substitute.For<ITrafficProviderFactory>();
        factory.GetProvider(Arg.Any<RouteProvider>()).Returns(provider);
        return factory;
    }

    // ── StartTripleTestCommand tests ──────────────────────────────────────────

    [Fact]
    public async Task StartTripleTest_WhenOriginGeocodeFails_ReturnsGeocodeError()
    {
        // Arrange
        TableStorageContext db = CreateDb();
        var fakeProvider = Substitute.For<ITrafficProvider>();
        fakeProvider.GeocodeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);  // geocode always fails

        ITrafficProviderFactory factory = BuildProviderFactory(fakeProvider);
        IJobScheduler scheduler = Substitute.For<IJobScheduler>();
        scheduler.Schedule(Arg.Any<Expression<Func<Task>>>(), Arg.Any<TimeSpan>()).Returns("fake-job-id");
        var handler = new StartTripleTestCommandHandler(
            db, factory, scheduler, NullLogger<StartTripleTestCommandHandler>.Instance);

        var command = new StartTripleTestCommand("Bad Origin", "Dest", RouteProvider.GoogleMaps, null);

        // Act
        StartTripleTestResult result = await handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("GEOCODE_FAILED_ORIGIN");
        result.SessionId.Should().BeNull();
    }

    [Fact]
    public async Task StartTripleTest_WhenDestinationGeocodeFails_ReturnsGeocodeError()
    {
        // Arrange
        TableStorageContext db = CreateDb();
        var fakeProvider = Substitute.For<ITrafficProvider>();
        fakeProvider.GeocodeAsync("Origin", Arg.Any<CancellationToken>()).Returns("1.0,1.0");
        fakeProvider.GeocodeAsync("BadDest", Arg.Any<CancellationToken>()).Returns((string?)null);

        ITrafficProviderFactory factory = BuildProviderFactory(fakeProvider);
        IJobScheduler scheduler = Substitute.For<IJobScheduler>();
        scheduler.Schedule(Arg.Any<Expression<Func<Task>>>(), Arg.Any<TimeSpan>()).Returns("fake-job-id");
        var handler = new StartTripleTestCommandHandler(
            db, factory, scheduler, NullLogger<StartTripleTestCommandHandler>.Instance);

        var command = new StartTripleTestCommand("Origin", "BadDest", RouteProvider.GoogleMaps, null);

        // Act
        StartTripleTestResult result = await handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("GEOCODE_FAILED_DESTINATION");
    }

    [Fact]
    public async Task StartTripleTest_WhenValid_CreatesSessionWith3ShotStubsAndSchedules3Jobs()
    {
        // Arrange
        TableStorageContext db = CreateDb();
        var fakeProvider = Substitute.For<ITrafficProvider>();
        fakeProvider.GeocodeAsync("A", Arg.Any<CancellationToken>()).Returns("1.0,1.0");
        fakeProvider.GeocodeAsync("B", Arg.Any<CancellationToken>()).Returns("2.0,2.0");

        ITrafficProviderFactory factory = BuildProviderFactory(fakeProvider);
        IJobScheduler scheduler = Substitute.For<IJobScheduler>();
        scheduler.Schedule(Arg.Any<Expression<Func<Task>>>(), Arg.Any<TimeSpan>()).Returns("fake-job-id");

        var handler = new StartTripleTestCommandHandler(
            db, factory, scheduler, NullLogger<StartTripleTestCommandHandler>.Instance);

        var command = new StartTripleTestCommand("A", "B", RouteProvider.GoogleMaps, null);

        // Act
        StartTripleTestResult result = await handler.Handle(command, CancellationToken.None);

        // Assert — session returned
        result.IsSuccess.Should().BeTrue();
        result.SessionId.Should().NotBeNull();

        // Assert — 3 shot stubs persisted in DB
        int shotCount = db.TripleTestShots
            .Count(s => s.SessionId == result.SessionId!.Value);
        shotCount.Should().Be(3);

        // Assert — shot offsets are 0, 20, 40 seconds
        List<int> offsets = db.TripleTestShots
            .Where(s => s.SessionId == result.SessionId!.Value)
            .OrderBy(s => s.ShotIndex)
            .Select(s => s.OffsetSeconds)
            .ToList();
        offsets.Should().BeEquivalentTo([0, 20, 40]);

        // Assert — all shots start with no results (pending)
        bool anyFired = db.TripleTestShots
            .Where(s => s.SessionId == result.SessionId!.Value)
            .Any(s => s.IsSuccess != null);
        anyFired.Should().BeFalse("shots should be unfired stubs");

        // Assert — 3 jobs were scheduled
        scheduler.Received(3).Schedule(Arg.Any<Expression<Func<Task>>>(), Arg.Any<TimeSpan>());
    }

    [Fact]
    public async Task StartTripleTest_WhenSameCoordinates_ReturnsSameCoordinatesError()
    {
        // Arrange
        TableStorageContext db = CreateDb();
        var fakeProvider = Substitute.For<ITrafficProvider>();
        fakeProvider.GeocodeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("1.0,1.0");  // both addresses resolve to same coords

        ITrafficProviderFactory factory = BuildProviderFactory(fakeProvider);
        IJobScheduler scheduler = Substitute.For<IJobScheduler>();
        var handler = new StartTripleTestCommandHandler(
            db, factory, scheduler, NullLogger<StartTripleTestCommandHandler>.Instance);

        var command = new StartTripleTestCommand("Same Place", "Same Place", RouteProvider.GoogleMaps, null);

        // Act
        StartTripleTestResult result = await handler.Handle(command, CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("SAME_COORDINATES");
    }

    // ── GetTripleTestSessionQuery tests ──────────────────────────────────────

    [Fact]
    public async Task GetTripleTestSession_WhenAllShotsSucceed_ReturnsCorrectWinnerAndAverage()
    {
        // Arrange
        TableStorageContext db = CreateDb();

        Guid sessionId = Guid.NewGuid();
        var session = new TripleTestSession
        {
            Id = sessionId,
            OriginAddress = "A",
            OriginCoordinates = "1,1",
            DestinationAddress = "B",
            DestinationCoordinates = "2,2",
            Provider = 0,
            ScheduledAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow
        };

        // Shot 0: 600s, Shot 1: 500s (WINNER), Shot 2: 700s
        DateTimeOffset now = DateTimeOffset.UtcNow;
        session.Shots.Add(new TripleTestShot { Id = Guid.NewGuid(), SessionId = sessionId, ShotIndex = 0, OffsetSeconds = 0, FiredAt = now, IsSuccess = true, DurationSeconds = 600, DistanceMetres = 10000 });
        session.Shots.Add(new TripleTestShot { Id = Guid.NewGuid(), SessionId = sessionId, ShotIndex = 1, OffsetSeconds = 20, FiredAt = now.AddSeconds(20), IsSuccess = true, DurationSeconds = 500, DistanceMetres = 10000 });
        session.Shots.Add(new TripleTestShot { Id = Guid.NewGuid(), SessionId = sessionId, ShotIndex = 2, OffsetSeconds = 40, FiredAt = now.AddSeconds(40), IsSuccess = true, DurationSeconds = 700, DistanceMetres = 10000 });
        db.Add(session);
        foreach (var shot in session.Shots) db.Add(shot);
        await db.SaveChangesAsync();

        var handler = new GetTripleTestSessionQueryHandler(db);

        // Act
        var dto = await handler.Handle(new GetTripleTestSessionQuery(sessionId), CancellationToken.None);

        // Assert — winner is shot 1 (500s)
        dto.Should().NotBeNull();
        dto!.IdealShotIndex.Should().Be(1, "shot 1 has the shortest duration of 500s");
        dto.AverageDurationSeconds.Should().BeApproximately((600 + 500 + 700) / 3.0, 0.01);
        dto.Shots.Should().HaveCount(3);
    }

    [Fact]
    public async Task GetTripleTestSession_WhenOneShotFails_WinnerFromRemaining()
    {
        // Arrange
        TableStorageContext db = CreateDb();

        Guid sessionId = Guid.NewGuid();
        var session = new TripleTestSession
        {
            Id = sessionId,
            OriginAddress = "A",
            OriginCoordinates = "1,1",
            DestinationAddress = "B",
            DestinationCoordinates = "2,2",
            Provider = 0,
            ScheduledAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow
        };

        DateTimeOffset now = DateTimeOffset.UtcNow;
        session.Shots.Add(new TripleTestShot { Id = Guid.NewGuid(), SessionId = sessionId, ShotIndex = 0, OffsetSeconds = 0, FiredAt = now, IsSuccess = true,  DurationSeconds = 800, DistanceMetres = 10000 });
        session.Shots.Add(new TripleTestShot { Id = Guid.NewGuid(), SessionId = sessionId, ShotIndex = 1, OffsetSeconds = 20, FiredAt = now.AddSeconds(20), IsSuccess = false, DurationSeconds = null, DistanceMetres = null, ErrorCode = "PROVIDER_ERROR" });
        session.Shots.Add(new TripleTestShot { Id = Guid.NewGuid(), SessionId = sessionId, ShotIndex = 2, OffsetSeconds = 40, FiredAt = now.AddSeconds(40), IsSuccess = true,  DurationSeconds = 600, DistanceMetres = 10000 });
        db.Add(session);
        foreach (var shot in session.Shots) db.Add(shot);
        await db.SaveChangesAsync();

        var handler = new GetTripleTestSessionQueryHandler(db);

        // Act
        var dto = await handler.Handle(new GetTripleTestSessionQuery(sessionId), CancellationToken.None);

        // Assert — shot 2 wins despite shot 0 also succeeding, because 600 < 800
        dto!.IdealShotIndex.Should().Be(2);
        dto.AverageDurationSeconds.Should().BeApproximately((800 + 600) / 2.0, 0.01,
            "average computed only from successful shots");
    }

    [Fact]
    public async Task GetTripleTestSession_WhenAllShotsFail_IdealShotIndexIsNull()
    {
        // Arrange
        TableStorageContext db = CreateDb();

        Guid sessionId = Guid.NewGuid();
        var session = new TripleTestSession
        {
            Id = sessionId,
            OriginAddress = "A",
            OriginCoordinates = "1,1",
            DestinationAddress = "B",
            DestinationCoordinates = "2,2",
            Provider = 0,
            ScheduledAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow
        };

        session.Shots.Add(new TripleTestShot { Id = Guid.NewGuid(), SessionId = sessionId, ShotIndex = 0, OffsetSeconds = 0, FiredAt = DateTimeOffset.UtcNow, IsSuccess = false, ErrorCode = "PROVIDER_ERROR" });
        session.Shots.Add(new TripleTestShot { Id = Guid.NewGuid(), SessionId = sessionId, ShotIndex = 1, OffsetSeconds = 20, FiredAt = DateTimeOffset.UtcNow, IsSuccess = false, ErrorCode = "PROVIDER_ERROR" });
        session.Shots.Add(new TripleTestShot { Id = Guid.NewGuid(), SessionId = sessionId, ShotIndex = 2, OffsetSeconds = 40, FiredAt = DateTimeOffset.UtcNow, IsSuccess = false, ErrorCode = "PROVIDER_ERROR" });
        db.Add(session);
        foreach (var shot in session.Shots) db.Add(shot);
        await db.SaveChangesAsync();

        var handler = new GetTripleTestSessionQueryHandler(db);

        // Act
        var dto = await handler.Handle(new GetTripleTestSessionQuery(sessionId), CancellationToken.None);

        // Assert
        dto!.IdealShotIndex.Should().BeNull("no successful shots means no winner");
        dto.AverageDurationSeconds.Should().BeNull();
        dto.AverageDistanceMetres.Should().BeNull();
    }

    [Fact]
    public async Task GetTripleTestSession_WhenSessionNotFound_ReturnsNull()
    {
        // Arrange
        TableStorageContext db = CreateDb();
        var handler = new GetTripleTestSessionQueryHandler(db);

        // Act
        var dto = await handler.Handle(new GetTripleTestSessionQuery(Guid.NewGuid()), CancellationToken.None);

        // Assert
        dto.Should().BeNull();
    }

    [Fact]
    public async Task GetTripleTestSession_WhenPartiallyCompleted_ReturnsPartialAverages()
    {
        // Arrange — only 1 of 3 shots complete
        TableStorageContext db = CreateDb();

        Guid sessionId = Guid.NewGuid();
        var session = new TripleTestSession
        {
            Id = sessionId,
            OriginAddress = "A",
            OriginCoordinates = "1,1",
            DestinationAddress = "B",
            DestinationCoordinates = "2,2",
            Provider = 0,
            ScheduledAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow
        };

        session.Shots.Add(new TripleTestShot { Id = Guid.NewGuid(), SessionId = sessionId, ShotIndex = 0, OffsetSeconds = 0, FiredAt = DateTimeOffset.UtcNow, IsSuccess = true, DurationSeconds = 400, DistanceMetres = 8000 });
        session.Shots.Add(new TripleTestShot { Id = Guid.NewGuid(), SessionId = sessionId, ShotIndex = 1, OffsetSeconds = 20 });
        session.Shots.Add(new TripleTestShot { Id = Guid.NewGuid(), SessionId = sessionId, ShotIndex = 2, OffsetSeconds = 40 });
        db.Add(session);
        foreach (var shot in session.Shots) db.Add(shot);
        await db.SaveChangesAsync();

        var handler = new GetTripleTestSessionQueryHandler(db);

        // Act
        var dto = await handler.Handle(new GetTripleTestSessionQuery(sessionId), CancellationToken.None);

        // Assert — partial results available
        dto!.IdealShotIndex.Should().Be(0, "only shot 0 has completed");
        dto.AverageDurationSeconds.Should().BeApproximately(400.0, 0.01);
        dto.Shots.Should().HaveCount(3, "all 3 shot stubs are returned regardless of completion");
    }
}
