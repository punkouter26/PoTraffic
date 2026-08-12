using FluentAssertions;
using PoTraffic.API.Features.History;
using PoTraffic.API.Infrastructure.Storage;
using PoTraffic.Shared.DTOs.History;
using PoTraffic.Shared.Enums;
using PoTraffic.UnitTests.Helpers;

namespace PoTraffic.UnitTests.Features.History;

/// <summary>
/// Tests for <see cref="GetVolatilityHeatmapQueryHandler"/> (#5) — the day-of-week × hour
/// grid behind the weekly congestion view.
/// </summary>
public sealed class GetVolatilityHeatmapHandlerTests
{
    /// <summary>A Tuesday, so the weekday axis is exercised by a real (fixed) date.</summary>
    private static readonly DateTimeOffset Tuesday = new(2026, 8, 4, 0, 0, 0, TimeSpan.Zero);

    private static (TableStorageContext Db, RouteId RouteId, UserId UserId) SeedRoute()
    {
        TableStorageContext db = TestDoubles.CreateDb();
        RouteId routeId = RouteId.New();
        UserId userId = UserId.New();

        db.Add(new EntityRoute
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
        });

        return (db, routeId, userId);
    }

    private static void AddPoll(
        TableStorageContext db, RouteId routeId, DateTimeOffset polledAt, int seconds, bool deleted = false)
        => db.Add(new PollRecord
        {
            Id = PollRecordId.New(),
            RouteId = routeId,
            PolledAt = polledAt,
            TravelDurationSeconds = seconds,
            DistanceMetres = 10_000,
            IsDeleted = deleted
        });

    [Fact]
    public async Task Heatmap_WhenRouteNotOwnedByCaller_ReturnsEmptyGrid()
    {
        (TableStorageContext db, RouteId routeId, _) = SeedRoute();
        AddPoll(db, routeId, Tuesday.AddHours(8), 900);
        await db.SaveChangesAsync();

        var handler = new GetVolatilityHeatmapQueryHandler(db);

        // Act — a different user asks for the same route
        VolatilityHeatmapDto result = await handler.Handle(
            new GetVolatilityHeatmapQuery(routeId, UserId.New()), CancellationToken.None);

        // Assert — IDOR guard: no cells, and nothing that hints the route exists
        result.Cells.Should().BeEmpty();
        result.TotalSamples.Should().Be(0);
        result.MedianDurationSeconds.Should().Be(0);
    }

    [Fact]
    public async Task Heatmap_GroupsSamplesIntoOneCellPerWeekdayHour()
    {
        (TableStorageContext db, RouteId routeId, UserId userId) = SeedRoute();

        // Two samples in the same hour, one in the next — two cells, not three.
        AddPoll(db, routeId, Tuesday.AddHours(8), 600);
        AddPoll(db, routeId, Tuesday.AddHours(8).AddMinutes(15), 800);
        AddPoll(db, routeId, Tuesday.AddHours(9), 1200);
        await db.SaveChangesAsync();

        var handler = new GetVolatilityHeatmapQueryHandler(db);

        VolatilityHeatmapDto result = await handler.Handle(
            new GetVolatilityHeatmapQuery(routeId, userId), CancellationToken.None);

        result.Cells.Should().HaveCount(2);
        result.TotalSamples.Should().Be(3);

        HeatmapCellDto eight = result.Cells.Single(c => c.Hour == 8);
        eight.DayOfWeek.Should().Be(Tuesday.DayOfWeek.ToString());
        eight.SampleCount.Should().Be(2);
        eight.MeanDurationSeconds.Should().Be(700);
        // Sample standard deviation over {600, 800}: sqrt(((−100)² + 100²) / 1).
        eight.StdDevDurationSeconds.Should().BeApproximately(141.42, 0.01);

        HeatmapCellDto nine = result.Cells.Single(c => c.Hour == 9);
        nine.SampleCount.Should().Be(1);
        nine.StdDevDurationSeconds.Should().Be(0, "a single sample has no spread");
    }

    [Fact]
    public async Task Heatmap_SeparatesTheSameHourOnDifferentWeekdays()
    {
        (TableStorageContext db, RouteId routeId, UserId userId) = SeedRoute();

        AddPoll(db, routeId, Tuesday.AddHours(8), 600);
        AddPoll(db, routeId, Tuesday.AddDays(1).AddHours(8), 1800);
        await db.SaveChangesAsync();

        var handler = new GetVolatilityHeatmapQueryHandler(db);

        VolatilityHeatmapDto result = await handler.Handle(
            new GetVolatilityHeatmapQuery(routeId, userId), CancellationToken.None);

        result.Cells.Should().HaveCount(2, "08:00 Tuesday and 08:00 Wednesday are different cells");
        result.Cells.Select(c => c.DayOfWeek).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task Heatmap_MedianIsNotDraggedUpByAnOutlierCommute()
    {
        (TableStorageContext db, RouteId routeId, UserId userId) = SeedRoute();

        // Three ordinary commutes and one disaster. The mean would be 2100s; the median
        // — what the colour ramp is measured against — must stay at the ordinary time.
        AddPoll(db, routeId, Tuesday.AddHours(8), 600);
        AddPoll(db, routeId, Tuesday.AddHours(9), 600);
        AddPoll(db, routeId, Tuesday.AddHours(10), 600);
        AddPoll(db, routeId, Tuesday.AddHours(11), 6000);
        await db.SaveChangesAsync();

        var handler = new GetVolatilityHeatmapQueryHandler(db);

        VolatilityHeatmapDto result = await handler.Handle(
            new GetVolatilityHeatmapQuery(routeId, userId), CancellationToken.None);

        result.MedianDurationSeconds.Should().Be(600);
    }

    [Fact]
    public async Task Heatmap_ExcludesSoftDeletedSamples()
    {
        (TableStorageContext db, RouteId routeId, UserId userId) = SeedRoute();

        AddPoll(db, routeId, Tuesday.AddHours(8), 600);
        AddPoll(db, routeId, Tuesday.AddHours(8).AddMinutes(30), 9000, deleted: true);
        await db.SaveChangesAsync();

        var handler = new GetVolatilityHeatmapQueryHandler(db);

        VolatilityHeatmapDto result = await handler.Handle(
            new GetVolatilityHeatmapQuery(routeId, userId), CancellationToken.None);

        result.TotalSamples.Should().Be(1);
        result.Cells.Single().MeanDurationSeconds.Should().Be(600, "pruned samples must not colour a cell");
    }

    [Fact]
    public async Task Heatmap_WhenRouteHasNoSamples_ReturnsEmptyGrid()
    {
        (TableStorageContext db, RouteId routeId, UserId userId) = SeedRoute();
        await db.SaveChangesAsync();

        var handler = new GetVolatilityHeatmapQueryHandler(db);

        VolatilityHeatmapDto result = await handler.Handle(
            new GetVolatilityHeatmapQuery(routeId, userId), CancellationToken.None);

        result.Cells.Should().BeEmpty();
        result.RouteId.Should().Be(routeId);
    }
}
