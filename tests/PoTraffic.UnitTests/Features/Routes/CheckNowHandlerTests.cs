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
/// Unit tests for <see cref="CheckNowCommandHandler"/>.
/// Verifies live travel-time retrieval without PollRecord persistence (FR-016),
/// ownership enforcement, and provider error handling.
/// </summary>
public sealed class CheckNowHandlerTests
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
    public async Task CheckNow_WhenProviderSucceeds_ReturnsTravelData_NoPollRecordInserted()
    {
        // Arrange
        string dbName = Guid.NewGuid().ToString();
        using PoTrafficDbContext db = CreateDb(dbName);

        Guid routeId = Guid.NewGuid();
        Guid userId  = Guid.NewGuid();

        db.Routes.Add(new EntityRoute
        {
            Id = routeId, UserId = userId, OriginAddress = "A", OriginCoordinates = "1.0,1.0",
            DestinationAddress = "B", DestinationCoordinates = "2.0,2.0",
            Provider = (int)RouteProvider.GoogleMaps, MonitoringStatus = (int)MonitoringStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        ITrafficProvider mockProvider = Substitute.For<ITrafficProvider>();
        mockProvider
            .GetTravelTimeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new TravelResult(600, 10_000, "{}"));

        var handler = new CheckNowCommandHandler(db, BuildProviderFactory(mockProvider),
            NullLogger<CheckNowCommandHandler>.Instance);

        // Act
        CheckNowResult result = await handler.Handle(new CheckNowCommand(routeId, userId), CancellationToken.None);

        // Assert — travel data returned
        result.IsSuccess.Should().BeTrue();
        result.DurationSeconds.Should().Be(600);
        result.DistanceMetres.Should().Be(10_000);
        result.ErrorCode.Should().BeNull();

        // FR-016: no PollRecord must be persisted
        int pollCount = await db.PollRecords.CountAsync();
        pollCount.Should().Be(0, "FR-016: CheckNow must never insert a PollRecord or consume quota");
    }

    [Fact]
    public async Task CheckNow_WhenRouteNotFound_ReturnsNotFoundError()
    {
        // Arrange
        string dbName = Guid.NewGuid().ToString();
        using PoTrafficDbContext db = CreateDb(dbName);

        ITrafficProvider mockProvider = Substitute.For<ITrafficProvider>();
        var handler = new CheckNowCommandHandler(db, BuildProviderFactory(mockProvider),
            NullLogger<CheckNowCommandHandler>.Instance);

        // Act — route does not exist
        CheckNowResult result = await handler.Handle(
            new CheckNowCommand(Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("NOT_FOUND");
    }

    [Fact]
    public async Task CheckNow_WhenProviderReturnsNull_ReturnsProviderError()
    {
        // Arrange
        string dbName = Guid.NewGuid().ToString();
        using PoTrafficDbContext db = CreateDb(dbName);

        Guid routeId = Guid.NewGuid();
        Guid userId  = Guid.NewGuid();

        db.Routes.Add(new EntityRoute
        {
            Id = routeId, UserId = userId, OriginAddress = "A", OriginCoordinates = "1.0,1.0",
            DestinationAddress = "B", DestinationCoordinates = "2.0,2.0",
            Provider = (int)RouteProvider.GoogleMaps, MonitoringStatus = (int)MonitoringStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        ITrafficProvider mockProvider = Substitute.For<ITrafficProvider>();
        mockProvider
            .GetTravelTimeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((TravelResult?)null);

        var handler = new CheckNowCommandHandler(db, BuildProviderFactory(mockProvider),
            NullLogger<CheckNowCommandHandler>.Instance);

        // Act
        CheckNowResult result = await handler.Handle(new CheckNowCommand(routeId, userId), CancellationToken.None);

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("PROVIDER_ERROR");
    }

    [Fact]
    public async Task CheckNow_WhenRouteBelongsToDifferentUser_ReturnsNotFoundError()
    {
        // Arrange
        string dbName = Guid.NewGuid().ToString();
        using PoTrafficDbContext db = CreateDb(dbName);

        Guid routeId   = Guid.NewGuid();
        Guid realOwner = Guid.NewGuid();

        db.Routes.Add(new EntityRoute
        {
            Id = routeId, UserId = realOwner, OriginAddress = "A", OriginCoordinates = "1.0,1.0",
            DestinationAddress = "B", DestinationCoordinates = "2.0,2.0",
            Provider = (int)RouteProvider.GoogleMaps, MonitoringStatus = (int)MonitoringStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        ITrafficProvider mockProvider = Substitute.For<ITrafficProvider>();
        var handler = new CheckNowCommandHandler(db, BuildProviderFactory(mockProvider),
            NullLogger<CheckNowCommandHandler>.Instance);

        // Act — different user ID supplied
        CheckNowResult result = await handler.Handle(
            new CheckNowCommand(routeId, Guid.NewGuid()), CancellationToken.None);

        // Assert — cross-user check must behave identically to not-found (no info leak)
        result.IsSuccess.Should().BeFalse();
        result.ErrorCode.Should().Be("NOT_FOUND");
    }
}
