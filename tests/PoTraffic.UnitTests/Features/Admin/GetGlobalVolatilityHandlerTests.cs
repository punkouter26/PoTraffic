using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PoTraffic.Api.Features.Admin;
using PoTraffic.Api.Infrastructure.Data;

using PoTraffic.Shared.DTOs.Admin;


namespace PoTraffic.UnitTests.Features.Admin;

/// <summary>
/// Unit tests for <see cref="GetGlobalVolatilityHandler"/>.
/// FR-024: global volatility aggregation groups PollRecords by DayOfWeek × TimeSlotBucket across ALL users.
/// </summary>
public sealed class GetGlobalVolatilityHandlerTests
{
    private static PoTrafficDbContext CreateDb(string name)
    {
        DbContextOptions<PoTrafficDbContext> opts = new DbContextOptionsBuilder<PoTrafficDbContext>()
            .UseInMemoryDatabase(name)
            .Options;
        return new PoTrafficDbContext(opts);
    }

    [Fact]
    public async Task GetGlobalVolatility_GroupsByDayOfWeekAndSlot()
    {
        // Arrange
        string dbName = Guid.NewGuid().ToString();
        await using PoTrafficDbContext db = CreateDb(dbName);

        Guid userId = Guid.NewGuid();
        Guid routeId = Guid.NewGuid();

        db.Users.Add(new User { Id = userId, Email = "test@test.com", PasswordHash = "x", Locale = "en-IE", CreatedAt = DateTimeOffset.UtcNow });
        db.Routes.Add(new EntityRoute
        {
            Id = routeId, UserId = userId,
            OriginAddress = "A", OriginCoordinates = "0,0",
            DestinationAddress = "B", DestinationCoordinates = "1,1",
            Provider = 0 // GoogleMaps
        });

        // Monday 08:00 = bucket 480, Monday 08:05 = bucket 485, Monday 08:00 again (second route)
        // Three records: 2 at slot 480, 1 at 485 — different routes (simulating multiple users)
        DateTimeOffset monday0800 = GetNextMonday().AddHours(8);
        DateTimeOffset monday0805 = monday0800.AddMinutes(5);

        db.PollRecords.AddRange([
            new PollRecord { Id = Guid.NewGuid(), RouteId = routeId, PolledAt = monday0800, TravelDurationSeconds = 300, DistanceMetres = 5000 },
            new PollRecord { Id = Guid.NewGuid(), RouteId = routeId, PolledAt = monday0800.AddDays(-7), TravelDurationSeconds = 320, DistanceMetres = 5000 }, // prev Monday same slot
            new PollRecord { Id = Guid.NewGuid(), RouteId = routeId, PolledAt = monday0805, TravelDurationSeconds = 360, DistanceMetres = 5000 },
        ]);
        await db.SaveChangesAsync();

        var handler = new GetGlobalVolatilityHandler(db, NullLogger<GetGlobalVolatilityHandler>.Instance);

        // Act
        IReadOnlyList<GlobalVolatilitySlotDto> result = await handler.Handle(new GetGlobalVolatilityQuery(), CancellationToken.None);

        // Assert — at least one slot returned, grouped by day+slot
        result.Should().NotBeEmpty();
        GlobalVolatilitySlotDto mondaySlot = result.First(s => s.TimeSlotBucket == 480);
        mondaySlot.MeanDurationSeconds.Should().BeApproximately(310.0, 1.0,
            "mean of 300 and 320 is 310");
    }

    [Fact]
    public async Task GetGlobalVolatility_WhenNoRecords_ReturnsEmpty()
    {
        string dbName = Guid.NewGuid().ToString();
        await using PoTrafficDbContext db = CreateDb(dbName);

        var handler = new GetGlobalVolatilityHandler(db, NullLogger<GetGlobalVolatilityHandler>.Instance);

        IReadOnlyList<GlobalVolatilitySlotDto> result = await handler.Handle(new GetGlobalVolatilityQuery(), CancellationToken.None);

        result.Should().BeEmpty("no poll records means no volatility slots");
    }

    private static DateTimeOffset GetNextMonday()
    {
        DateTimeOffset today = DateTimeOffset.UtcNow.Date;
        int daysUntilMonday = ((int)DayOfWeek.Monday - (int)today.DayOfWeek + 7) % 7;
        if (daysUntilMonday == 0) daysUntilMonday = 7;
        return today.AddDays(-daysUntilMonday); // last Monday
    }
}
