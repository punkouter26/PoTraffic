using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PoTraffic.API.Features.Admin;
using PoTraffic.API.Infrastructure.Storage;

using PoTraffic.Shared.DTOs.Admin;


namespace PoTraffic.UnitTests.Features.Admin;

/// <summary>
/// Unit tests for <see cref="GetPollCostSummaryHandler"/>.
/// FR-023: EstimatedCostUsd = TodayPollCount × cost.perpoll.{provider}.
/// </summary>
public sealed class GetPollCostSummaryHandlerTests
{
    private static TableStorageContext CreateDb()
    {
        return new TableStorageContext();
    }

    [Fact]
    public async Task GetPollCostSummary_ComputesCorrectCost_ForGoogleMaps()
    {
        // Arrange
        TableStorageContext db = CreateDb();

        UserId userId = UserId.New();
        RouteId routeId = RouteId.New();
        DateTimeOffset todayStart = DateTimeOffset.UtcNow.Date;

        // Need a User and Route for the navigation to work
        db.Add(new User { Id = userId, Email = "admin@test.com", Locale = "en-IE", CreatedAt = DateTimeOffset.UtcNow });
        db.Add(new EntityRoute
        {
            Id = routeId,
            UserId = userId,
            OriginAddress = "A",
            OriginCoordinates = "0,0",
            DestinationAddress = "B",
            DestinationCoordinates = "1,1",
            Provider = 0 // GoogleMaps
        });

        // Manually seed cost config (EnsureCreated may not run HasData in InMemory)
        db.AddRange(new PollRecord[]
        {
            new PollRecord { Id = PollRecordId.New(), RouteId = routeId, PolledAt = todayStart.AddHours(8), TravelDurationSeconds = 300, DistanceMetres = 5000 },
            new PollRecord { Id = PollRecordId.New(), RouteId = routeId, PolledAt = todayStart.AddHours(9), TravelDurationSeconds = 310, DistanceMetres = 5000 }
        });
        db.SeedDefaultConfigurationsIfMissing();
        await db.SaveChangesAsync();

        var handler = new GetPollCostSummaryHandler(db, NullLogger<GetPollCostSummaryHandler>.Instance);

        // Act
        IReadOnlyList<PollCostSummaryDto> result = await handler.Handle(new GetPollCostSummaryQuery(), CancellationToken.None);

        // Assert — total cost = 2 × 0.005 = 0.010 (Google Maps cost from seed)
        result.Should().NotBeEmpty();
        double totalCost = result.Sum(r => r.TotalEstimatedCostUsd);
        totalCost.Should().BeApproximately(0.01, 0.001,
            "2 polls × $0.005 (cost.perpoll.googlemaps) should equal $0.010");
    }

    [Fact]
    public async Task GetPollCostSummary_WhenNoPollsToday_ReturnsSummaryWithZeroCost()
    {
        TableStorageContext db = CreateDb();

        var handler = new GetPollCostSummaryHandler(db, NullLogger<GetPollCostSummaryHandler>.Instance);

        IReadOnlyList<PollCostSummaryDto> result = await handler.Handle(new GetPollCostSummaryQuery(), CancellationToken.None);

        result.Should().NotBeNull();
        result.Sum(r => r.TotalPollCount).Should().Be(0,
            "no poll records seeded → zero total polls");
    }
}
