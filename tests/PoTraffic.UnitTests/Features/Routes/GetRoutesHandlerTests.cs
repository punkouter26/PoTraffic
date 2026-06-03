using FluentAssertions;
using PoTraffic.Api.Features.Routes;
using PoTraffic.Api.Infrastructure.Storage;

using PoTraffic.Shared.Enums;


namespace PoTraffic.UnitTests.Features.Routes;

/// <summary>
/// Unit tests for <see cref="GetRoutesQueryHandler"/>.
/// Verifies pagination, user-scoping, and soft-deleted route exclusion.
/// </summary>
public sealed class GetRoutesHandlerTests
{
    private static TableStorageContext CreateDb()
    {
        return new TableStorageContext();
    }

    [Fact]
    public async Task GetRoutes_ReturnsOnlyRoutesForRequestedUser()
    {
        // Arrange
        using TableStorageContext db = CreateDb();

        Guid userId = Guid.NewGuid();
        Guid otherId = Guid.NewGuid();

        db.AddRange(new[] { , ,  })MonitoringStatus.Active,
                CreatedAt = DateTimeOffset.UtcNow
            },
            new EntityRoute
            {
                Id = Guid.NewGuid(),
                UserId = otherId,
                OriginAddress = "C",
                DestinationAddress = "D",
                Provider = (int)RouteProvider.TomTom,
                MonitoringStatus = (int)MonitoringStatus.Active,
                CreatedAt = DateTimeOffset.UtcNow
            });
        await db.SaveChangesAsync();

        var handler = new GetRoutesQueryHandler(db);

        // Act
        var result = await handler.Handle(new GetRoutesQuery(userId, 1, 20), CancellationToken.None);

        // Assert
        result.Items.Should().HaveCount(1, "only the requesting user's routes should be returned");
        result.Items[0].OriginAddress.Should().Be("A", "the route belonging to the requesting user should be returned");
        result.TotalCount.Should().Be(1);
    }

    [Fact]
    public async Task GetRoutes_ExcludesSoftDeletedRoutes()
    {
        // Arrange
        using TableStorageContext db = CreateDb();

        Guid userId = Guid.NewGuid();

        db.AddRange(new[] { , ,  })MonitoringStatus.Active,
                CreatedAt = DateTimeOffset.UtcNow
            },
            new EntityRoute
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                OriginAddress = "C",
                DestinationAddress = "D",
                Provider = (int)RouteProvider.GoogleMaps,
                MonitoringStatus = (int)MonitoringStatus.Deleted,
                CreatedAt = DateTimeOffset.UtcNow
            });
        await db.SaveChangesAsync();

        var handler = new GetRoutesQueryHandler(db);

        // Act
        var result = await handler.Handle(new GetRoutesQuery(userId, 1, 20), CancellationToken.None);

        // Assert
        result.Items.Should().HaveCount(1, "soft-deleted routes must be excluded from results");
        result.Items[0].MonitoringStatus.Should().NotBe(MonitoringStatus.Deleted);
    }

    [Fact]
    public async Task GetRoutes_ReturnsEmptyPage_WhenUserHasNoRoutes()
    {
        // Arrange
        using TableStorageContext db = CreateDb();

        var handler = new GetRoutesQueryHandler(db);

        // Act
        var result = await handler.Handle(new GetRoutesQuery(Guid.NewGuid(), 1, 20), CancellationToken.None);

        // Assert
        result.Items.Should().BeEmpty();
        result.TotalCount.Should().Be(0);
    }

    [Fact]
    public async Task GetRoutes_PaginatesCorrectly()
    {
        // Arrange
        using TableStorageContext db = CreateDb();

        Guid userId = Guid.NewGuid();

        for (int i = 0; i < 5; i++)
        {
            db.Add(new EntityRoute
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                OriginAddress = $"Origin{i}",
                DestinationAddress = $"Dest{i}",
                Provider = (int)RouteProvider.GoogleMaps,
                MonitoringStatus = (int)MonitoringStatus.Active,
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-i) // different timestamps for ordering
            });
        }
        await db.SaveChangesAsync();

        var handler = new GetRoutesQueryHandler(db);

        // Act — page 2, page size 2
        var result = await handler.Handle(new GetRoutesQuery(userId, 2, 2), CancellationToken.None);

        // Assert
        result.TotalCount.Should().Be(5);
        result.Page.Should().Be(2);
        result.PageSize.Should().Be(2);
        result.Items.Should().HaveCount(2, "page 2 of 5 items with page-size 2 must return 2 items");
    }
}
