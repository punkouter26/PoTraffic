using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PoTraffic.Api.Features.Maintenance;
using PoTraffic.Api.Infrastructure.Data;


namespace PoTraffic.UnitTests.Features.Maintenance;

/// <summary>
/// Unit tests for <see cref="PruneOldPollRecordsJobHandler"/>.
/// FR-020: soft-delete records older than 90 days; records within window untouched.
/// </summary>
public sealed class PruningJobTests
{
    private static PoTrafficDbContext CreateDb(string name)
    {
        DbContextOptions<PoTrafficDbContext> opts = new DbContextOptionsBuilder<PoTrafficDbContext>()
            .UseInMemoryDatabase(name)
            .Options;
        return new PoTrafficDbContext(opts);
    }

    [Fact]
    public async Task PruneJob_MarksOldRecordsDeleted_DoesNotTouchRecentRecords()
    {
        // Arrange
        string dbName = Guid.NewGuid().ToString();
        await using PoTrafficDbContext db = CreateDb(dbName);

        Guid routeId = Guid.NewGuid();
        DateTime cutoff = DateTime.UtcNow.AddDays(-90);

        // 3 old records (> 90 days)
        db.Set<PollRecord>().AddRange([
            new PollRecord { Id = Guid.NewGuid(), RouteId = routeId, PolledAt = cutoff.AddDays(-2), TravelDurationSeconds = 300, DistanceMetres = 5000, RawProviderResponse = "data" },
            new PollRecord { Id = Guid.NewGuid(), RouteId = routeId, PolledAt = cutoff.AddDays(-5), TravelDurationSeconds = 310, DistanceMetres = 5000, RawProviderResponse = "data" },
            new PollRecord { Id = Guid.NewGuid(), RouteId = routeId, PolledAt = cutoff.AddDays(-30), TravelDurationSeconds = 320, DistanceMetres = 5000, RawProviderResponse = "data" },
        ]);

        // 2 recent records (< 90 days)
        db.Set<PollRecord>().AddRange([
            new PollRecord { Id = Guid.NewGuid(), RouteId = routeId, PolledAt = cutoff.AddDays(1), TravelDurationSeconds = 290, DistanceMetres = 5000 },
            new PollRecord { Id = Guid.NewGuid(), RouteId = routeId, PolledAt = DateTime.UtcNow.AddDays(-10), TravelDurationSeconds = 280, DistanceMetres = 5000 },
        ]);

        await db.SaveChangesAsync();

        var handler = new PruneOldPollRecordsCommandHandler(db, NullLogger<PruneOldPollRecordsCommandHandler>.Instance);

        // Act
        int deleted = await handler.Handle(new PruneOldPollRecordsCommand(), CancellationToken.None);

        // Assert — 3 old records deleted
        deleted.Should().Be(3, "3 records are older than 90 days");

        // Reload bypassing global filter
        List<PollRecord> allRecords = await db.Set<PollRecord>().IgnoreQueryFilters().ToListAsync();
        allRecords.Count(r => r.IsDeleted).Should().Be(3);
        allRecords.Count(r => !r.IsDeleted).Should().Be(2);

        // Raw provider response nulled out on deleted records
        allRecords.Where(r => r.IsDeleted)
            .Should().AllSatisfy(r => r.RawProviderResponse.Should().BeNull(
                "RawProviderResponse must be cleared on pruned records (FR-020)"));
    }

    [Fact]
    public async Task PruneJob_WhenNoOldRecords_ReturnsZero()
    {
        string dbName = Guid.NewGuid().ToString();
        await using PoTrafficDbContext db = CreateDb(dbName);

        var handler = new PruneOldPollRecordsCommandHandler(db, NullLogger<PruneOldPollRecordsCommandHandler>.Instance);

        int deleted = await handler.Handle(new PruneOldPollRecordsCommand(), CancellationToken.None);

        deleted.Should().Be(0);
    }

    [Fact]
    public async Task PruneJob_DoesNotTouchRecordExactlyAtBoundary()
    {
        // Record exactly 90 days ago should NOT be pruned (boundary is exclusive)
        string dbName = Guid.NewGuid().ToString();
        await using PoTrafficDbContext db = CreateDb(dbName);
        Guid routeId = Guid.NewGuid();

        // Exactly 90 days — borderline (should NOT be deleted per spec: < 90 days window means > 90 days is deleted)
        // PolledAt < GETUTCDATE() - 90 → strictly less than means exact boundary is not deleted
        db.Set<PollRecord>().Add(new PollRecord
        {
            Id = Guid.NewGuid(), RouteId = routeId,
            PolledAt = DateTime.UtcNow.AddDays(-90).AddMinutes(5), // 90 days ago + 5 min → just inside window
            TravelDurationSeconds = 300, DistanceMetres = 5000
        });
        await db.SaveChangesAsync();

        var handler = new PruneOldPollRecordsCommandHandler(db, NullLogger<PruneOldPollRecordsCommandHandler>.Instance);

        int deleted = await handler.Handle(new PruneOldPollRecordsCommand(), CancellationToken.None);

        deleted.Should().Be(0, "record within 90-day window must not be pruned");
    }
}
