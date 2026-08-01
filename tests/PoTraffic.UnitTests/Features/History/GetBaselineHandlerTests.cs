using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PoTraffic.API.Features.History;
using PoTraffic.API.Infrastructure.Storage;

using PoTraffic.Shared.DTOs.History;
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
    private static TableStorageContext CreateDb()
    {
        return new TableStorageContext();
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
        TableStorageContext db = CreateDb();

        RouteId routeId = RouteId.New();
        var handler = new GetBaselineQueryHandler(db, NullLogger<GetBaselineQueryHandler>.Instance);

        // Act
        var result = await handler.Handle(
            new GetBaselineQuery(routeId, UserId.Empty, "Monday"),
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
        TableStorageContext db = CreateDb();
        RouteId routeId = RouteId.New();

        var handler = new GetBaselineQueryHandler(db, NullLogger<GetBaselineQueryHandler>.Instance);

        // Act
        var result = await handler.Handle(
            new GetBaselineQuery(routeId, UserId.Empty, "Tuesday"),
            CancellationToken.None);

        // Assert
        result.RouteId.Should().Be(routeId);
        result.DayOfWeek.Should().Be("Tuesday");
    }
}
