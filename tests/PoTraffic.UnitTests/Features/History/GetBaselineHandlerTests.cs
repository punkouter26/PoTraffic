using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PoTraffic.Api.Features.History;
using PoTraffic.Api.Infrastructure.Data;

using PoTraffic.Api.Infrastructure.Data.Projections;
using PoTraffic.Shared.Constants;
using PoTraffic.Shared.Enums;

namespace PoTraffic.UnitTests.Features.History;

/// <summary>
/// Tests for <see cref="GetBaselineQueryHandler"/>.
/// FR-012 / SC-002: baseline requires ≥ BaselineMinSessionCount (3) distinct sessions.
/// When fewer sessions exist, the handler must return an empty slot list.
/// </summary>
public sealed class GetBaselineHandlerTests
{
    private static PoTrafficDbContext CreateDb(string name)
    {
        DbContextOptions<PoTrafficDbContext> opts = new DbContextOptionsBuilder<PoTrafficDbContext>()
            .UseInMemoryDatabase(name)
            .Options;
        return new PoTrafficDbContext(opts);
    }

    /// <summary>
    /// FR-012: fewer than 3 distinct sessions for a slot → null StdDevDurationSeconds.
    /// In the InMemory DB, we cannot test the raw SQL path, but we can test the handler's
    /// response when the DB returns empty (i.e., the SQL returns no rows due to HAVING COUNT ≥ 3).
    /// This test validates handler null-safety and empty-list handling.
    /// </summary>
    [Fact]
    public async Task GetBaselineHandler_WhenFewerThanThreeSessions_ReturnsEmptySlotList()
    {
        // Arrange
        string dbName = Guid.NewGuid().ToString();
        PoTrafficDbContext db = CreateDb(dbName);

        Guid routeId = Guid.NewGuid();
        var handler = new GetBaselineQueryHandler(db, NullLogger<GetBaselineQueryHandler>.Instance);

        // Act
        var result = await handler.Handle(
            new GetBaselineQuery(routeId, "Monday"),
            CancellationToken.None);

        // Assert — no sessions → baseline response has empty slot list
        result.Should().NotBeNull();
        result.Slots.Should().BeEmpty(
            "fewer than 3 sessions cannot satisfy FR-012 HAVING COUNT(DISTINCT date) >= 3");
        result.RouteId.Should().Be(routeId);
    }

    [Fact]
    public async Task GetBaselineHandler_ReturnsCorrectRouteId()
    {
        // Arrange
        string dbName = Guid.NewGuid().ToString();
        PoTrafficDbContext db = CreateDb(dbName);
        Guid routeId = Guid.NewGuid();

        var handler = new GetBaselineQueryHandler(db, NullLogger<GetBaselineQueryHandler>.Instance);

        // Act
        var result = await handler.Handle(
            new GetBaselineQuery(routeId, "Tuesday"),
            CancellationToken.None);

        // Assert
        result.RouteId.Should().Be(routeId);
        result.DayOfWeek.Should().Be("Tuesday");
    }
}
