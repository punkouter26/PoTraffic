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
    private static readonly DateTimeOffset TuesdayUtc = new(2026, 8, 4, 0, 0, 0, TimeSpan.Zero);

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
        TableStorageContext db, RouteId routeId, DateTimeOffset polledAt, int seconds)
        => db.Add(new PollRecord
        {
            Id = PollRecordId.New(),
            RouteId = routeId,
            PolledAt = polledAt,
            TravelDurationSeconds = seconds,
            DistanceMetres = 10_000
        });

    /// <summary>
    /// Fixed Eastern → UTC offset for the assertions below. August 2026 is in EDT
    /// (UTC−4); the handler resolves this dynamically, but the tests anchor on the same
    /// instant the production code reads against.
    /// </summary>
    private static readonly TimeSpan EasternOffset = TimeSpan.FromHours(-4);
    private static DateTimeOffset Eastern(DateTimeOffset utc) => utc.ToOffset(EasternOffset);

    [Fact]
    public async Task Heatmap_WhenRouteNotOwnedByCaller_ReturnsEmptyGrid()
    {
        (TableStorageContext db, RouteId routeId, _) = SeedRoute();
        AddPoll(db, routeId, TuesdayUtc.AddHours(12), 900);
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
    public async Task Heatmap_GroupsSamplesIntoOneCellPerWeekdayHalfHour()
    {
        (TableStorageContext db, RouteId routeId, UserId userId) = SeedRoute();

        // Three samples land in three different cells: 12:00 UTC → 08:00 EDT first half-hour,
        // 12:15 UTC → 08:15 EDT (same half-hour), 13:00 UTC → 09:00 EDT first half-hour.
        AddPoll(db, routeId, TuesdayUtc.AddHours(12), 600);
        AddPoll(db, routeId, TuesdayUtc.AddHours(12).AddMinutes(15), 800);
        AddPoll(db, routeId, TuesdayUtc.AddHours(13), 1200);
        await db.SaveChangesAsync();

        var handler = new GetVolatilityHeatmapQueryHandler(db);

        VolatilityHeatmapDto result = await handler.Handle(
            new GetVolatilityHeatmapQuery(routeId, userId), CancellationToken.None);

        result.Cells.Should().HaveCount(2);
        result.TotalSamples.Should().Be(3);

        HeatmapCellDto eight = result.Cells.Single(c => c.Hour == 8 && c.HalfHour == 0);
        eight.DayOfWeek.Should().Be(TuesdayUtc.DayOfWeek.ToString());
        eight.SampleCount.Should().Be(2);
        eight.MeanDurationSeconds.Should().Be(700);
        // Sample standard deviation over {600, 800}: sqrt(((−100)² + 100²) / 1).
        eight.StdDevDurationSeconds.Should().BeApproximately(141.42, 0.01);

        HeatmapCellDto nine = result.Cells.Single(c => c.Hour == 9 && c.HalfHour == 0);
        nine.SampleCount.Should().Be(1);
        nine.StdDevDurationSeconds.Should().Be(0, "a single sample has no spread");
    }

    [Fact]
    public async Task Heatmap_BucketingHalfHourSlotsSeparatesMinutes0to29From30to59()
    {
        (TableStorageContext db, RouteId routeId, UserId userId) = SeedRoute();

        // 14:15 UTC = 10:15 EDT (first half-hour), 14:45 UTC = 10:45 EDT (second).
        AddPoll(db, routeId, TuesdayUtc.AddHours(14).AddMinutes(15), 600);
        AddPoll(db, routeId, TuesdayUtc.AddHours(14).AddMinutes(45), 900);
        await db.SaveChangesAsync();

        var handler = new GetVolatilityHeatmapQueryHandler(db);

        VolatilityHeatmapDto result = await handler.Handle(
            new GetVolatilityHeatmapQuery(routeId, userId), CancellationToken.None);

        result.Cells.Should().HaveCount(2, "10:15 and 10:45 fall in different half-hour slots");
        result.Cells.Should().ContainSingle(c => c.Hour == 10 && c.HalfHour == 0 && c.MeanDurationSeconds == 600);
        result.Cells.Should().ContainSingle(c => c.Hour == 10 && c.HalfHour == 1 && c.MeanDurationSeconds == 900);
    }

    [Fact]
    public async Task Heatmap_SeparatesTheSameHourOnDifferentWeekdays()
    {
        (TableStorageContext db, RouteId routeId, UserId userId) = SeedRoute();

        AddPoll(db, routeId, TuesdayUtc.AddHours(12), 600);
        AddPoll(db, routeId, TuesdayUtc.AddDays(1).AddHours(12), 1800);
        await db.SaveChangesAsync();

        var handler = new GetVolatilityHeatmapQueryHandler(db);

        VolatilityHeatmapDto result = await handler.Handle(
            new GetVolatilityHeatmapQuery(routeId, userId), CancellationToken.None);

        result.Cells.Should().HaveCount(2, "08:00 EDT Tuesday and 08:00 EDT Wednesday are different cells");
        result.Cells.Select(c => c.DayOfWeek).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task Heatmap_MedianIsNotDraggedUpByAnOutlierCommute()
    {
        (TableStorageContext db, RouteId routeId, UserId userId) = SeedRoute();

        // Three ordinary commutes and one disaster. The mean would be 2100s; the median
        // — what the colour ramp is measured against — must stay at the ordinary time.
        AddPoll(db, routeId, TuesdayUtc.AddHours(12), 600);
        AddPoll(db, routeId, TuesdayUtc.AddHours(13), 600);
        AddPoll(db, routeId, TuesdayUtc.AddHours(14), 600);
        AddPoll(db, routeId, TuesdayUtc.AddHours(15), 6000);
        await db.SaveChangesAsync();

        var handler = new GetVolatilityHeatmapQueryHandler(db);

        VolatilityHeatmapDto result = await handler.Handle(
            new GetVolatilityHeatmapQuery(routeId, userId), CancellationToken.None);

        result.MedianDurationSeconds.Should().Be(600);
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
