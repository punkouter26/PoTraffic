using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PoTraffic.Api.Features.Routes;
using PoTraffic.Api.Infrastructure.Data;

using PoTraffic.Api.Infrastructure.Providers;
using PoTraffic.Shared.Enums;

namespace PoTraffic.UnitTests.Features.Routes;

public sealed class ExecutePollHandlerTests
{
    private static PoTrafficDbContext CreateDb(string name)
    {
        DbContextOptions<PoTrafficDbContext> opts = new DbContextOptionsBuilder<PoTrafficDbContext>()
            .UseInMemoryDatabase(name)
            .Options;
        return new PoTrafficDbContext(opts);
    }

    private static ITrafficProviderFactory BuildProviderFactory(ITrafficProvider provider)
    {
        var factory = Substitute.For<ITrafficProviderFactory>();
        factory.GetProvider(Arg.Any<RouteProvider>()).Returns(provider);
        return factory;
    }

    [Fact]
    public async Task ExecutePollHandler_WhenProviderSucceeds_RecordsPollData()
    {
        // Arrange
        string dbName = Guid.NewGuid().ToString();
        using PoTrafficDbContext db = CreateDb(dbName);

        Guid routeId = Guid.NewGuid();
        Guid sessionId = Guid.NewGuid();

        db.Routes.Add(new Route
        {
            Id = routeId,
            UserId = Guid.NewGuid(),
            OriginAddress = "A",
            OriginCoordinates = "1.0,1.0",
            DestinationAddress = "B",
            DestinationCoordinates = "2.0,2.0",
            Provider = (int)RouteProvider.GoogleMaps,
            MonitoringStatus = (int)MonitoringStatus.Active,
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

        ITrafficProvider mockProvider = Substitute.For<ITrafficProvider>();
        mockProvider
            .GetTravelTimeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new TravelResult(300, 5000, "{}"));

        ITrafficProviderFactory providerFactory = BuildProviderFactory(mockProvider);

        var handler = new ExecutePollCommandHandler(db, providerFactory, NullLogger<ExecutePollCommandHandler>.Instance);

        // Act
        bool result = await handler.Handle(new ExecutePollCommand(routeId), CancellationToken.None);

        // Assert
        result.Should().BeTrue();

        PollRecord? record = await db.PollRecords.FirstOrDefaultAsync(p => p.RouteId == routeId);
        record.Should().NotBeNull();
        record!.TravelDurationSeconds.Should().Be(300);
        record.DistanceMetres.Should().Be(5000);
        record.PolledAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
        record.SessionId.Should().Be(sessionId);
    }
}
