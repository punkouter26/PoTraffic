using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PoTraffic.Api.Features.Routes;
using PoTraffic.Api.Infrastructure.Storage;
using PoTraffic.Api.Infrastructure.Scheduling;

using PoTraffic.Shared.Enums;


namespace PoTraffic.UnitTests.Features.Routes;

/// <summary>
/// Unit tests for <see cref="DeleteRouteCommandHandler"/>.
/// Verifies soft-delete semantics, job cancellation, and ownership enforcement.
/// </summary>
public sealed class DeleteRouteHandlerTests
{
    private static TableStorageContext CreateDb()
    {
        return new TableStorageContext();
    }

    [Fact]
    public async Task DeleteRoute_SoftDeletesRoute_AndReturnsTrue()
    {
        // Arrange
        TableStorageContext db = CreateDb();

        Guid routeId = Guid.NewGuid();
        Guid userId = Guid.NewGuid();

        db.Add(new EntityRoute
        {
            Id = routeId,
            UserId = userId,
            OriginAddress = "A",
            DestinationAddress = "B",
            Provider = (int)RouteProvider.GoogleMaps,
            MonitoringStatus = (int)MonitoringStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        IJobScheduler scheduler = Substitute.For<IJobScheduler>();
        var handler = new DeleteRouteCommandHandler(db, scheduler, NullLogger<DeleteRouteCommandHandler>.Instance);

        // Act
        bool result = await handler.Handle(new DeleteRouteCommand(routeId, userId), CancellationToken.None);

        // Assert
        result.Should().BeTrue();

        EntityRoute? route = db.Routes.FirstOrDefault(x => x.Id == routeId);
        route!.MonitoringStatus.Should().Be((int)MonitoringStatus.Deleted, "route must be soft-deleted");
        route.JobChainId.Should().BeNull("JobChainId must be cleared on soft-delete");
    }

    [Fact]
    public async Task DeleteRoute_CancelsJob_WhenJobChainIdIsSet()
    {
        // Arrange
        TableStorageContext db = CreateDb();

        Guid routeId = Guid.NewGuid();
        Guid userId = Guid.NewGuid();
        const string jobId = "job-42";

        db.Add(new EntityRoute
        {
            Id = routeId,
            UserId = userId,
            OriginAddress = "A",
            DestinationAddress = "B",
            Provider = (int)RouteProvider.GoogleMaps,
            MonitoringStatus = (int)MonitoringStatus.Active,
            JobChainId = jobId,
            CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        IJobScheduler scheduler = Substitute.For<IJobScheduler>();
        var handler = new DeleteRouteCommandHandler(db, scheduler, NullLogger<DeleteRouteCommandHandler>.Instance);

        // Act
        await handler.Handle(new DeleteRouteCommand(routeId, userId), CancellationToken.None);

        // Assert — job must be cancelled
        scheduler.Received(1).Cancel(jobId);
    }

    [Fact]
    public async Task DeleteRoute_WhenRouteNotFound_ReturnsFalse()
    {
        // Arrange
        TableStorageContext db = CreateDb();

        IJobScheduler scheduler = Substitute.For<IJobScheduler>();
        var handler = new DeleteRouteCommandHandler(db, scheduler, NullLogger<DeleteRouteCommandHandler>.Instance);

        // Act — no routes in DB
        bool result = await handler.Handle(
            new DeleteRouteCommand(Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None);

        // Assert
        result.Should().BeFalse("attempting to delete a non-existent route must return false");
    }

    [Fact]
    public async Task DeleteRoute_WhenRouteOwnedByDifferentUser_ReturnsFalse()
    {
        // Arrange
        TableStorageContext db = CreateDb();

        Guid routeId = Guid.NewGuid();
        Guid realOwner = Guid.NewGuid();

        db.Add(new EntityRoute
        {
            Id = routeId,
            UserId = realOwner,
            OriginAddress = "A",
            DestinationAddress = "B",
            Provider = (int)RouteProvider.GoogleMaps,
            MonitoringStatus = (int)MonitoringStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        IJobScheduler scheduler = Substitute.For<IJobScheduler>();
        var handler = new DeleteRouteCommandHandler(db, scheduler, NullLogger<DeleteRouteCommandHandler>.Instance);

        // Act — different user ID
        bool result = await handler.Handle(
            new DeleteRouteCommand(routeId, Guid.NewGuid()), CancellationToken.None);

        // Assert — ownership check must block unauthorised deletion
        result.Should().BeFalse("a user must not be able to delete another user's route");

        EntityRoute? route = db.Routes.FirstOrDefault(x => x.Id == routeId);
        route!.MonitoringStatus.Should().Be((int)MonitoringStatus.Active, "route must remain intact");
    }
}
