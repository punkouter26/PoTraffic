using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using PoTraffic.API.Features.Routes;
using PoTraffic.API.Infrastructure.Storage;

using PoTraffic.API.Infrastructure.Providers;
using PoTraffic.Shared.Enums;
using PoTraffic.UnitTests.Helpers;

namespace PoTraffic.UnitTests.Features.Routes;

public sealed class ExecutePollHandlerTests
{
    [Fact]
    public async Task ExecutePollHandler_WhenProviderSucceeds_RecordsPollData()
    {
        // Arrange
        TableStorageContext db = TestDoubles.CreateDb();

        RouteId routeId = RouteId.New();
        SessionId sessionId = SessionId.New();

        db.Add(new Route
        {
            Id = routeId,
            UserId = UserId.New(),
            OriginAddress = "A",
            OriginCoordinates = "1.0,1.0",
            DestinationAddress = "B",
            DestinationCoordinates = "2.0,2.0",
            Provider = (int)RouteProvider.GoogleMaps,
            MonitoringStatus = (int)MonitoringStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow
        });

        db.Add(new MonitoringSession
        {
            Id = sessionId,
            RouteId = routeId,
            SessionDate = DateOnly.FromDateTime(DateTime.UtcNow),
            State = (int)SessionState.Active
        });

        await db.SaveChangesAsync();

        ITrafficProvider mockProvider = Substitute.For<ITrafficProvider>();
        mockProvider
            .GetTravelTimeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new TravelResult(300, 5000, "{}"));


        var handler = PoTraffic.UnitTests.Helpers.PollHandlerTestHelper.Create(db, mockProvider);

        // Act
        bool result = await handler.Handle(new ExecutePollCommand(routeId), CancellationToken.None);

        // Assert
        result.Should().BeTrue();

        PollRecord? record = db.PollRecords.FirstOrDefault(p => p.RouteId == routeId);
        record.Should().NotBeNull();
        record!.TravelDurationSeconds.Should().Be(300);
        record.DistanceMetres.Should().Be(5000);
        record.PolledAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
        record.SessionId.Should().Be(sessionId);
    }

    // FR-005: a provider exception must not propagate out of the handler — it is caught,
    // logged as a warning, and the poll is skipped without touching the session.
    private static async Task<(TableStorageContext Db, RouteId RouteId, SessionId SessionId)> SeedAsync()
    {
        TableStorageContext db = TestDoubles.CreateDb();
        RouteId routeId = RouteId.New();
        SessionId sessionId = SessionId.New();

        db.Add(new Route
        {
            Id = routeId,
            UserId = UserId.New(),
            OriginAddress = "A",
            OriginCoordinates = "1.0,1.0",
            DestinationAddress = "B",
            DestinationCoordinates = "2.0,2.0",
            Provider = (int)RouteProvider.GoogleMaps,
            MonitoringStatus = (int)MonitoringStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow
        });

        db.Add(new MonitoringSession
        {
            Id = sessionId,
            RouteId = routeId,
            SessionDate = DateOnly.FromDateTime(DateTime.UtcNow),
            State = (int)SessionState.Active,
            PollCount = 3
        });

        await db.SaveChangesAsync();
        return (db, routeId, sessionId);
    }

    [Fact]
    public async Task WhenProviderThrowsHttpRequestException_ReturnsFalse_NoPollRecordInserted()
    {
        // Arrange
        (TableStorageContext db, RouteId routeId, SessionId sessionId) = await SeedAsync();

        ITrafficProvider mockProvider = Substitute.For<ITrafficProvider>();
        mockProvider
            .GetTravelTimeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Connection refused"));

        ILogger<ExecutePollCommandHandler> logger = Substitute.For<ILogger<ExecutePollCommandHandler>>();
        var handler = PoTraffic.UnitTests.Helpers.PollHandlerTestHelper.Create(db, mockProvider, logger);

        // Act
        bool result = await handler.Handle(new ExecutePollCommand(routeId), CancellationToken.None);

        // Assert — FR-005, every consequence of a thrown provider call in one place.
        // Reaching this line at all is the "no exception propagates" assertion.
        result.Should().BeFalse("provider errors must not propagate to caller (FR-005)");

        int pollCount = db.PollRecords.Count(p => p.RouteId == routeId);
        pollCount.Should().Be(0, "no PollRecord should be inserted when provider throws (FR-005)");

        // The seeded session already carries 3 polls, so the contract is "unchanged", not
        // "zero" — a failed provider call must neither add to nor reset the running count.
        MonitoringSession session = db.MonitoringSessions.Single(s => s.Id == sessionId);
        session.PollCount.Should().Be(3, "a failed provider call must not count against the session");

        logger.ReceivedWithAnyArgs().Log(
            LogLevel.Warning, default, default!, default, default!);
    }
}
