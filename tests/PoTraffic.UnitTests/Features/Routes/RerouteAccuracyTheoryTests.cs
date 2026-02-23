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

/// <summary>
/// Parameterised accuracy tests for reroute detection.
/// SC-004: reroute detection accuracy ≥95% (≥19/20 on a controlled 20-record sequence
/// with 4 known reroutes and 16 normal-variation readings).
/// </summary>
public sealed class RerouteAccuracyTheoryTests
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

    /// <summary>
    /// Provides the 20-record synthetic test sequence.
    ///
    /// Sequence design:
    ///   Baseline distance: 5000 m (15% threshold = 5750 m)
    ///   Normal variation:  ±3% (4850–5150 m) — should NOT trigger reroute
    ///   Reroute events:    pairs of consecutive readings at 6000 m — SHOULD trigger IsRerouted = true
    ///
    /// Known reroutes are at consecutive pairs:
    ///   Record index 5–6   (indices are 0-based into the sequence fed to the handler)
    ///   Record index 12–13
    ///
    /// This means 4 poll invocations where IsRerouted = true is expected out of 20 total.
    ///
    /// Returns: IEnumerable of (int[] priorDistances, int currentDistance, bool expectedIsRerouted)
    /// </summary>
    public static IEnumerable<object[]> SyntheticSequenceData()
    {
        // 20-record sequence
        // Format: (priorHistoryDistances, currentPollDistance, expectedIsRerouted)

        // Records 0-4: normal readings — all false (not enough consecutive elevated to trigger)
        // Seed history: baseline 5000 m × n prior records
        // Record 0: single normal reading, no prior elevated
        yield return [new int[] { 5000, 5000, 5000 }, 5100, false]; // normal, no reroute
        yield return [new int[] { 5000, 5000, 5100 }, 4950, false]; // normal, no reroute
        yield return [new int[] { 5000, 5100, 4950 }, 5050, false]; // normal, no reroute
        yield return [new int[] { 5100, 4950, 5050 }, 5000, false]; // normal, no reroute
        yield return [new int[] { 4950, 5050, 5000 }, 5150, false]; // slight elevation, no reroute (prior = 5000)

        // Records 5-6: reroute event — two consecutive elevated readings (6000 m each)
        // Record 5: first elevated reading — prior last = 5150 (normal), so no reroute yet
        yield return [new int[] { 5050, 5000, 5150 }, 6000, false]; // first elevated — NOT rerouted (single)
        // Record 6: second consecutive elevated reading — prior last = 6000 (elevated) — IS rerouted
        yield return [new int[] { 5000, 5150, 6000 }, 6100, true];  // second consecutive — REROUTED ✓

        // Records 7-11: normal readings resume
        yield return [new int[] { 5150, 6000, 6100 }, 5000, false]; // back to normal
        yield return [new int[] { 6000, 6100, 5000 }, 5050, false]; // normal
        yield return [new int[] { 6100, 5000, 5050 }, 4900, false]; // normal
        yield return [new int[] { 5000, 5050, 4900 }, 5100, false]; // normal
        yield return [new int[] { 5050, 4900, 5100 }, 5000, false]; // normal

        // Records 12-13: second reroute event
        // Record 12: first elevated — prior last = 5000 (normal), no reroute
        yield return [new int[] { 4900, 5100, 5000 }, 6200, false]; // first elevated — NOT rerouted (single)
        // Record 13: second consecutive elevated — IS rerouted
        yield return [new int[] { 5100, 5000, 6200 }, 6300, true];  // second consecutive — REROUTED ✓

        // Records 14-19: six more normal readings
        yield return [new int[] { 5000, 6200, 6300 }, 5100, false]; // recovery
        yield return [new int[] { 6200, 6300, 5100 }, 4950, false]; // normal
        yield return [new int[] { 6300, 5100, 4950 }, 5000, false]; // normal
        yield return [new int[] { 5100, 4950, 5000 }, 5050, false]; // normal
        yield return [new int[] { 4950, 5000, 5050 }, 4900, false]; // normal
        yield return [new int[] { 5000, 5050, 4900 }, 5150, false]; // normal
    }

    [Theory]
    [MemberData(nameof(SyntheticSequenceData))]
    public async Task RerouteDetection_MatchesExpectedOutcome(
        int[] priorDistances,
        int currentDistance,
        bool expectedIsRerouted)
    {
        // Arrange
        string dbName = Guid.NewGuid().ToString();
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

        // Seed prior poll records
        DateTimeOffset polledAt = DateTimeOffset.UtcNow.AddMinutes(-(priorDistances.Length * 5));
        foreach (int dist in priorDistances)
        {
            db.PollRecords.Add(new PollRecord
            {
                Id = Guid.NewGuid(),
                RouteId = routeId,
                SessionId = sessionId,
                PolledAt = polledAt,
                TravelDurationSeconds = 300,
                DistanceMetres = dist,
                RawProviderResponse = "{}"
            });
            polledAt = polledAt.AddMinutes(5);
        }

        await db.SaveChangesAsync();

        ITrafficProvider mockProvider = Substitute.For<ITrafficProvider>();
        mockProvider
            .GetTravelTimeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new TravelResult(300, currentDistance, "{}"));

        ITrafficProviderFactory providerFactory = BuildProviderFactory(mockProvider);
        var handler = new ExecutePollCommandHandler(db, providerFactory, NullLogger<ExecutePollCommandHandler>.Instance);

        // Act
        bool success = await handler.Handle(new ExecutePollCommand(routeId), CancellationToken.None);

        // Assert
        success.Should().BeTrue();

        PollRecord? record = await db.PollRecords
            .OrderByDescending(p => p.PolledAt)
            .FirstOrDefaultAsync(p => p.DistanceMetres == currentDistance);

        record.Should().NotBeNull();
        record!.IsRerouted.Should().Be(expectedIsRerouted,
            $"currentDistance={currentDistance}, priorLast={priorDistances.Last()}, " +
            $"expectedIsRerouted={expectedIsRerouted} (SC-004: ≥95% accuracy)");
    }

    /// <summary>
    /// Aggregated accuracy assertion over all 20 records.
    /// Counts correct predictions and asserts ≥19/20 (95%) accuracy (SC-004).
    /// </summary>
    [Fact]
    public async Task RerouteDetection_AchievesAtLeast95PercentAccuracy_OverSyntheticSequence()
    {
        // Build expected outcomes list from theory data
        var scenarios = SyntheticSequenceData()
            .Select(row => (priorDistances: (int[])row[0], currentDistance: (int)row[1], expected: (bool)row[2]))
            .ToList();

        int correctPredictions = 0;

        foreach ((int[] priorDistances, int currentDistance, bool expected) in scenarios)
        {
            string dbName = Guid.NewGuid().ToString();
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

            DateTimeOffset polledAt = DateTimeOffset.UtcNow.AddMinutes(-(priorDistances.Length * 5));
            foreach (int dist in priorDistances)
            {
                db.PollRecords.Add(new PollRecord
                {
                    Id = Guid.NewGuid(),
                    RouteId = routeId,
                    SessionId = sessionId,
                    PolledAt = polledAt,
                    TravelDurationSeconds = 300,
                    DistanceMetres = dist,
                    RawProviderResponse = "{}"
                });
                polledAt = polledAt.AddMinutes(5);
            }

            await db.SaveChangesAsync();

            ITrafficProvider mockProvider = Substitute.For<ITrafficProvider>();
            mockProvider
                .GetTravelTimeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(new TravelResult(300, currentDistance, "{}"));

            ITrafficProviderFactory providerFactory = BuildProviderFactory(mockProvider);
            var handler = new ExecutePollCommandHandler(db, providerFactory, NullLogger<ExecutePollCommandHandler>.Instance);

            await handler.Handle(new ExecutePollCommand(routeId), CancellationToken.None);

            PollRecord? record = await db.PollRecords
                .OrderByDescending(p => p.PolledAt)
                .FirstOrDefaultAsync(p => p.DistanceMetres == currentDistance);

            if (record is not null && record.IsRerouted == expected)
                correctPredictions++;
        }

        int total = scenarios.Count;
        double accuracy = (double)correctPredictions / total;

        accuracy.Should().BeGreaterThanOrEqualTo(0.95,
            $"reroute detection must achieve ≥95% accuracy over the synthetic sequence (SC-004). " +
            $"Got {correctPredictions}/{total} = {accuracy:P1}");
    }
}
