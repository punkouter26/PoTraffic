using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PoTraffic.Api.Features.Admin;
using PoTraffic.Api.Infrastructure.Data;

using PoTraffic.Shared.DTOs.Admin;


namespace PoTraffic.UnitTests.Features.Admin;

/// <summary>
/// Unit tests for <see cref="GetPollCostSummaryHandler"/>.
/// FR-023: EstimatedCostUsd = TodayPollCount × cost.perpoll.{provider}.
/// </summary>
public sealed class GetPollCostSummaryHandlerTests
{
    private static PoTrafficDbContext CreateDb(string name)
    {
        DbContextOptions<PoTrafficDbContext> opts = new DbContextOptionsBuilder<PoTrafficDbContext>()
            .UseInMemoryDatabase(name)
            .Options;
        return new PoTrafficDbContext(opts);
    }

    [Fact]
    public async Task GetPollCostSummary_ComputesCorrectCost_ForGoogleMaps()
    {
        // Arrange
        string dbName = Guid.NewGuid().ToString();
        await using PoTrafficDbContext db = CreateDb(dbName);

        Guid userId = Guid.NewGuid();
        Guid routeId = Guid.NewGuid();
        DateTimeOffset todayStart = DateTimeOffset.UtcNow.Date;

        // Need a User and Route for the navigation to work
        db.Users.Add(new User { Id = userId, Email = "admin@test.com", PasswordHash = "x", Locale = "en-IE", CreatedAt = DateTimeOffset.UtcNow });
        db.Routes.Add(new EntityRoute
        {
            Id = routeId, UserId = userId,
            OriginAddress = "A", OriginCoordinates = "0,0",
            DestinationAddress = "B", DestinationCoordinates = "1,1",
            Provider = 0 // GoogleMaps
        });

        // Manually seed cost config (EnsureCreated may not run HasData in InMemory)
        db.SystemConfigurations.AddRange([
            new SystemConfiguration { Key = "cost.perpoll.googlemaps", Value = "0.005", IsSensitive = false },
            new SystemConfiguration { Key = "cost.perpoll.tomtom",     Value = "0.004", IsSensitive = false },
        ]);

        // 2 polls today for Google Maps route (Provider = 0)
        db.PollRecords.AddRange([
            new PollRecord { Id = Guid.NewGuid(), RouteId = routeId, PolledAt = todayStart.AddHours(8), TravelDurationSeconds = 300, DistanceMetres = 5000 },
            new PollRecord { Id = Guid.NewGuid(), RouteId = routeId, PolledAt = todayStart.AddHours(9), TravelDurationSeconds = 310, DistanceMetres = 5000 },
        ]);
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
        string dbName = Guid.NewGuid().ToString();
        await using PoTrafficDbContext db = CreateDb(dbName);

        var handler = new GetPollCostSummaryHandler(db, NullLogger<GetPollCostSummaryHandler>.Instance);

        IReadOnlyList<PollCostSummaryDto> result = await handler.Handle(new GetPollCostSummaryQuery(), CancellationToken.None);

        result.Should().NotBeNull();
        result.Sum(r => r.TotalPollCount).Should().Be(0,
            "no poll records seeded → zero total polls");
    }
}
