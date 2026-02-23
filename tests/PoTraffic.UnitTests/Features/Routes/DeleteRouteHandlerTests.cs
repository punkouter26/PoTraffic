using FluentAssertions;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PoTraffic.Api.Features.Routes;
using PoTraffic.Api.Infrastructure.Data;

using PoTraffic.Shared.Enums;


namespace PoTraffic.UnitTests.Features.Routes;

/// <summary>
/// Unit tests for <see cref="DeleteRouteCommandHandler"/>.
/// Verifies soft-delete semantics, Hangfire job cancellation, and ownership enforcement.
/// </summary>
public sealed class DeleteRouteHandlerTests
{
    private static PoTrafficDbContext CreateDb(string name)
    {
        DbContextOptions<PoTrafficDbContext> opts = new DbContextOptionsBuilder<PoTrafficDbContext>()
            .UseInMemoryDatabase(name)
            .Options;
        return new PoTrafficDbContext(opts);
    }

    [Fact]
    public async Task DeleteRoute_SoftDeletesRoute_AndReturnsTrue()
    {
        // Arrange
        string dbName = Guid.NewGuid().ToString();
        using PoTrafficDbContext db = CreateDb(dbName);

        Guid routeId = Guid.NewGuid();
        Guid userId  = Guid.NewGuid();

        db.Routes.Add(new EntityRoute
        {
            Id = routeId, UserId = userId, OriginAddress = "A", DestinationAddress = "B",
            Provider = (int)RouteProvider.GoogleMaps, MonitoringStatus = (int)MonitoringStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        IBackgroundJobClient jobClient = Substitute.For<IBackgroundJobClient>();
        var handler = new DeleteRouteCommandHandler(db, jobClient, NullLogger<DeleteRouteCommandHandler>.Instance);

        // Act
        bool result = await handler.Handle(new DeleteRouteCommand(routeId, userId), CancellationToken.None);

        // Assert
        result.Should().BeTrue();

        EntityRoute? route = await db.Routes.FindAsync(routeId);
        route!.MonitoringStatus.Should().Be((int)MonitoringStatus.Deleted, "route must be soft-deleted");
        route.HangfireJobChainId.Should().BeNull("HangfireJobChainId must be cleared on soft-delete");
    }

    [Fact]
    public async Task DeleteRoute_CancelsHangfireJob_WhenJobChainIdIsSet()
    {
        // Arrange
        string dbName = Guid.NewGuid().ToString();
        using PoTrafficDbContext db = CreateDb(dbName);

        Guid routeId = Guid.NewGuid();
        Guid userId  = Guid.NewGuid();
        const string jobId = "hangfire-job-42";

        db.Routes.Add(new EntityRoute
        {
            Id = routeId, UserId = userId, OriginAddress = "A", DestinationAddress = "B",
            Provider = (int)RouteProvider.GoogleMaps, MonitoringStatus = (int)MonitoringStatus.Active,
            HangfireJobChainId = jobId,
            CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        IBackgroundJobClient jobClient = Substitute.For<IBackgroundJobClient>();
        var handler = new DeleteRouteCommandHandler(db, jobClient, NullLogger<DeleteRouteCommandHandler>.Instance);

        // Act
        await handler.Handle(new DeleteRouteCommand(routeId, userId), CancellationToken.None);

        // Assert — Hangfire job must be cancelled.
        // IBackgroundJobClient.Delete() is an extension method (not interceptable); verify the
        // underlying ChangeState call that the extension delegates to. Proxy pattern — NSubstitute
        // can only capture virtual/interface members.
        jobClient.Received(1).ChangeState(jobId, Arg.Any<Hangfire.States.IState>(), Arg.Any<string>());
    }

    [Fact]
    public async Task DeleteRoute_WhenRouteNotFound_ReturnsFalse()
    {
        // Arrange
        string dbName = Guid.NewGuid().ToString();
        using PoTrafficDbContext db = CreateDb(dbName);

        IBackgroundJobClient jobClient = Substitute.For<IBackgroundJobClient>();
        var handler = new DeleteRouteCommandHandler(db, jobClient, NullLogger<DeleteRouteCommandHandler>.Instance);

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
        string dbName = Guid.NewGuid().ToString();
        using PoTrafficDbContext db = CreateDb(dbName);

        Guid routeId = Guid.NewGuid();
        Guid realOwner = Guid.NewGuid();

        db.Routes.Add(new EntityRoute
        {
            Id = routeId, UserId = realOwner, OriginAddress = "A", DestinationAddress = "B",
            Provider = (int)RouteProvider.GoogleMaps, MonitoringStatus = (int)MonitoringStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        IBackgroundJobClient jobClient = Substitute.For<IBackgroundJobClient>();
        var handler = new DeleteRouteCommandHandler(db, jobClient, NullLogger<DeleteRouteCommandHandler>.Instance);

        // Act — different user ID
        bool result = await handler.Handle(
            new DeleteRouteCommand(routeId, Guid.NewGuid()), CancellationToken.None);

        // Assert — ownership check must block unauthorised deletion
        result.Should().BeFalse("a user must not be able to delete another user's route");

        EntityRoute? route = await db.Routes.FindAsync(routeId);
        route!.MonitoringStatus.Should().Be((int)MonitoringStatus.Active, "route must remain intact");
    }
}
