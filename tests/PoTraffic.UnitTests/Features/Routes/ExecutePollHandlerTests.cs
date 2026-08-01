using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
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

        ITrafficProviderFactory providerFactory = TestDoubles.ProviderFactory(mockProvider);

        var handler = new ExecutePollCommandHandler(db, providerFactory, PoTraffic.UnitTests.Helpers.AlertTestHelper.NoOp(db), NullLogger<ExecutePollCommandHandler>.Instance);

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
}
