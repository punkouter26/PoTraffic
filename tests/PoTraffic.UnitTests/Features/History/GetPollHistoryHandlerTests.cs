using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PoTraffic.API.Features.History;
using PoTraffic.API.Infrastructure.Storage;

using PoTraffic.Shared.Enums;
using PoTraffic.UnitTests.Helpers;

namespace PoTraffic.UnitTests.Features.History;

/// <summary>
/// Tests for <see cref="GetPollHistoryQueryHandler"/>.
/// Verifies pagination and ownership filtering.
/// </summary>
public sealed class GetPollHistoryHandlerTests
{

    private static async Task<(TableStorageContext Db, UserId UserId, RouteId RouteId, RouteId OtherRouteId)> SeedAsync(
        int pollCount = 5)
    {
        TableStorageContext db = TestDoubles.CreateDb();
        UserId userId = UserId.New();
        RouteId routeId = RouteId.New();
        RouteId otherRouteId = RouteId.New();

        var route = new Route
        {
            Id = routeId,
            UserId = userId,
            OriginAddress = "A",
            OriginCoordinates = "1.0,1.0",
            DestinationAddress = "B",
            DestinationCoordinates = "2.0,2.0",
            Provider = (int)RouteProvider.GoogleMaps,
            MonitoringStatus = (int)MonitoringStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.Add(route);

        var otherRoute = new Route
        {
            Id = otherRouteId,
            UserId = UserId.New(),
            OriginAddress = "C",
            OriginCoordinates = "3.0,3.0",
            DestinationAddress = "D",
            DestinationCoordinates = "4.0,4.0",
            Provider = (int)RouteProvider.TomTom,
            MonitoringStatus = (int)MonitoringStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.Add(otherRoute);

        DateTimeOffset polledAt = DateTimeOffset.UtcNow.AddMinutes(-pollCount * 5);
        for (int i = 0; i < pollCount; i++)
        {
            db.Add(new PollRecord
            {
                Id = PollRecordId.New(),
                RouteId = routeId,
                Route = route,
                PolledAt = polledAt.AddMinutes(i * 5),
                TravelDurationSeconds = 300 + i,
                DistanceMetres = 5000
            });
        }

        // Seed poll records for ANOTHER route — should NOT appear in results
        db.Add(new PollRecord
        {
            Id = PollRecordId.New(),
            RouteId = otherRouteId,
            Route = otherRoute,
            PolledAt = DateTimeOffset.UtcNow,
            TravelDurationSeconds = 999,
            DistanceMetres = 9999
        });

        await db.SaveChangesAsync();
        return (db, userId, routeId, otherRouteId);
    }

    [Fact]
    public async Task GetPollHistory_ReturnsOnlyRecordsForRequestedRoute()
    {
        // Arrange
        (TableStorageContext db, UserId userId, RouteId routeId, _) = await SeedAsync(pollCount: 5);
        var handler = new GetPollHistoryQueryHandler(db);

        // Act
        var result = await handler.Handle(
            new GetPollHistoryQuery(routeId, UserId: userId, Page: 1, PageSize: 20),
            CancellationToken.None);

        // Assert
        result.Items.Should().HaveCount(5);
        result.Items.Should().AllSatisfy(r => r.TravelDurationSeconds.Should().BeGreaterThanOrEqualTo(300));
    }

    [Fact]
    public async Task GetPollHistory_PaginatesCorrectly()
    {
        // Arrange
        (TableStorageContext db, UserId userId, RouteId routeId, _) = await SeedAsync(pollCount: 10);
        var handler = new GetPollHistoryQueryHandler(db);

        // Act — page 1 of 3 records each
        var page1 = await handler.Handle(
            new GetPollHistoryQuery(routeId, userId, Page: 1, PageSize: 3),
            CancellationToken.None);

        var page2 = await handler.Handle(
            new GetPollHistoryQuery(routeId, userId, Page: 2, PageSize: 3),
            CancellationToken.None);

        // Assert
        page1.Items.Should().HaveCount(3);
        page2.Items.Should().HaveCount(3);
        page1.TotalCount.Should().Be(10);
        page2.TotalCount.Should().Be(10);

        // Records on page 1 and page 2 should be distinct
        page1.Items.Select(r => r.Id).Should().NotIntersectWith(page2.Items.Select(r => r.Id));
    }

    [Fact]
    public async Task GetPollHistory_WhenNoRecords_ReturnsEmptyPagedResult()
    {
        // Arrange
        TableStorageContext db = TestDoubles.CreateDb();
        var handler = new GetPollHistoryQueryHandler(db);

        // Act
        var result = await handler.Handle(
            new GetPollHistoryQuery(RouteId.New(), UserId.New(), Page: 1, PageSize: 20),
            CancellationToken.None);

        // Assert
        result.Items.Should().BeEmpty();
        result.TotalCount.Should().Be(0);
    }
}
