using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PoTraffic.Api.Features.Routes;
using PoTraffic.Api.Infrastructure.Data;

using PoTraffic.Api.Infrastructure.Providers;
using PoTraffic.Shared.Constants;
using PoTraffic.Shared.Enums;

namespace PoTraffic.UnitTests.Features.Routes;

/// <summary>
/// Tests for reroute detection logic in <see cref="ExecutePollCommandHandler"/>.
/// FR-006: two consecutive readings ≥15% above session median → IsRerouted = true.
/// SC-004: detection accuracy ≥95%.
/// </summary>
public sealed class RerouteDetectionTests
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

    private static async Task<(PoTrafficDbContext Db, Guid RouteId, Guid SessionId)> SeedBaseAsync(
        string dbName,
        IEnumerable<int> priorDistances)
    {
        PoTrafficDbContext db = CreateDb(dbName);
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

        // Seed prior poll records for the session
        DateTimeOffset polledBase = DateTimeOffset.UtcNow.AddMinutes(-priorDistances.Count() * 5);
        foreach (int dist in priorDistances)
        {
            db.PollRecords.Add(new PollRecord
            {
                Id = Guid.NewGuid(),
                RouteId = routeId,
                SessionId = sessionId,
                PolledAt = polledBase,
                TravelDurationSeconds = 300,
                DistanceMetres = dist,
                RawProviderResponse = "{}"
            });
            polledBase = polledBase.AddMinutes(5);
        }

        await db.SaveChangesAsync();
        return (db, routeId, sessionId);
    }

    [Fact]
    public async Task TwoConsecutiveElevatedReadings_AboveMedianThreshold_SetsIsReroutedTrue()
    {
        // Arrange
        // Baseline: 10 readings at 5000 m → median = 5000 m
        // Threshold = 5000 * 1.15 = 5750 m
        // Prior reading (most recent) = 6000 m (elevated)
        // Current poll: 6200 m (elevated) — second consecutive → IsRerouted = true
        int[] priorDistances = [5000, 5000, 5000, 5000, 5000, 5000, 5000, 5000, 5000, 6000];
        string dbName = Guid.NewGuid().ToString();
        (PoTrafficDbContext db, Guid routeId, Guid sessionId) = await SeedBaseAsync(dbName, priorDistances);

        // Current poll returns 6200 m — elevated
        ITrafficProvider mockProvider = Substitute.For<ITrafficProvider>();
        mockProvider
            .GetTravelTimeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new TravelResult(320, 6200, "{}"));

        ITrafficProviderFactory providerFactory = BuildProviderFactory(mockProvider);
        var handler = new ExecutePollCommandHandler(db, providerFactory, NullLogger<ExecutePollCommandHandler>.Instance);

        // Act
        bool result = await handler.Handle(new ExecutePollCommand(routeId), CancellationToken.None);

        // Assert
        result.Should().BeTrue();
        PollRecord? newRecord = await db.PollRecords
            .OrderByDescending(p => p.PolledAt)
            .FirstOrDefaultAsync(p => p.DistanceMetres == 6200);
        newRecord.Should().NotBeNull();
        newRecord!.IsRerouted.Should().BeTrue(
            "two consecutive readings ≥15% above median should flag a reroute (FR-006)");
    }

    [Fact]
    public async Task SingleElevatedReading_DoesNotSetIsRerouted()
    {
        // Arrange
        // Baseline: 10 readings at 5000 m → median = 5000 m
        // Prior reading (most recent) = 5000 m (normal)
        // Current poll: 6200 m (elevated but FIRST elevated) — only one → IsRerouted = false
        int[] priorDistances = [5000, 5000, 5000, 5000, 5000, 5000, 5000, 5000, 5000, 5000];
        string dbName = Guid.NewGuid().ToString();
        (PoTrafficDbContext db, Guid routeId, _) = await SeedBaseAsync(dbName, priorDistances);

        ITrafficProvider mockProvider = Substitute.For<ITrafficProvider>();
        mockProvider
            .GetTravelTimeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new TravelResult(320, 6200, "{}"));

        ITrafficProviderFactory providerFactory = BuildProviderFactory(mockProvider);
        var handler = new ExecutePollCommandHandler(db, providerFactory, NullLogger<ExecutePollCommandHandler>.Instance);

        // Act
        bool result = await handler.Handle(new ExecutePollCommand(routeId), CancellationToken.None);

        // Assert
        result.Should().BeTrue();
        PollRecord? newRecord = await db.PollRecords
            .OrderByDescending(p => p.PolledAt)
            .FirstOrDefaultAsync(p => p.DistanceMetres == 6200);
        newRecord.Should().NotBeNull();
        newRecord!.IsRerouted.Should().BeFalse(
            "a single elevated reading without a prior elevated reading should NOT flag a reroute (FR-006)");
    }

    [Fact]
    public async Task InsufficientPriorRecords_DoesNotSetIsRerouted()
    {
        // Arrange — only one prior record (need ≥2 to evaluate reroute)
        int[] priorDistances = [5000];
        string dbName = Guid.NewGuid().ToString();
        (PoTrafficDbContext db, Guid routeId, _) = await SeedBaseAsync(dbName, priorDistances);

        ITrafficProvider mockProvider = Substitute.For<ITrafficProvider>();
        mockProvider
            .GetTravelTimeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new TravelResult(320, 6200, "{}"));

        ITrafficProviderFactory providerFactory = BuildProviderFactory(mockProvider);
        var handler = new ExecutePollCommandHandler(db, providerFactory, NullLogger<ExecutePollCommandHandler>.Instance);

        // Act
        bool result = await handler.Handle(new ExecutePollCommand(routeId), CancellationToken.None);

        // Assert
        result.Should().BeTrue();
        PollRecord? newRecord = await db.PollRecords
            .OrderByDescending(p => p.PolledAt)
            .FirstOrDefaultAsync(p => p.DistanceMetres == 6200);
        newRecord.Should().NotBeNull();
        newRecord!.IsRerouted.Should().BeFalse(
            "fewer than 2 prior records are insufficient to evaluate reroute detection (FR-006)");
    }

    /// <summary>
    /// Tests the internal median calculator directly (unit coverage for CalculateMedian helper).
    /// </summary>
    [Theory]
    [InlineData(new double[] { 5000, 5000, 5000 }, 5000)]
    [InlineData(new double[] { 4000, 5000, 6000 }, 5000)]
    [InlineData(new double[] { 4000, 6000 }, 5000)]
    [InlineData(new double[] { 1000 }, 1000)]
    public void CalculateMedian_ReturnsCorrectMedian(double[] values, double expected)
    {
        double median = ExecutePollCommandHandler.CalculateMedian([.. values]);
        median.Should().BeApproximately(expected, 0.01);
    }
}
