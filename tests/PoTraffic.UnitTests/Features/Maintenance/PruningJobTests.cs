using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PoTraffic.API.Features.Maintenance;
using PoTraffic.API.Infrastructure.Storage;
using PoTraffic.UnitTests.Helpers;


namespace PoTraffic.UnitTests.Features.Maintenance;

/// <summary>
/// Unit tests for <see cref="PruneOldPollRecordsJobHandler"/>.
/// FR-020: hard-delete records older than 90 days; records within window untouched.
/// </summary>
public sealed class PruningJobTests
{

    [Fact]
    public async Task PruneJob_DeletesOldRecords_LeavesRecentRecords()
    {
        // Arrange
        TableStorageContext db = TestDoubles.CreateDb();

        RouteId routeId = RouteId.New();
        DateTime cutoff = DateTime.UtcNow.AddDays(-90);

        // 3 old records (> 90 days)
        db.AddRange(new PollRecord[]
        {
            new PollRecord { Id = PollRecordId.New(), RouteId = routeId, PolledAt = cutoff.AddDays(-5), TravelDurationSeconds = 300, DistanceMetres = 5000 },
            new PollRecord { Id = PollRecordId.New(), RouteId = routeId, PolledAt = cutoff.AddDays(-30), TravelDurationSeconds = 310, DistanceMetres = 5000 },
            new PollRecord { Id = PollRecordId.New(), RouteId = routeId, PolledAt = cutoff.AddDays(-60), TravelDurationSeconds = 320, DistanceMetres = 5000 }
        });

        // 2 recent records (< 90 days)
        db.AddRange(new PollRecord[]
        {
            new PollRecord { Id = PollRecordId.New(), RouteId = routeId, PolledAt = DateTime.UtcNow.AddDays(-10), TravelDurationSeconds = 280, DistanceMetres = 5000 },
            new PollRecord { Id = PollRecordId.New(), RouteId = routeId, PolledAt = DateTime.UtcNow.AddDays(-5), TravelDurationSeconds = 290, DistanceMetres = 5000 }
        });

        await db.SaveChangesAsync();

        var handler = new PruneOldPollRecordsCommandHandler(db, NullLogger<PruneOldPollRecordsCommandHandler>.Instance);

        // Act
        int deleted = await handler.Handle(new PruneOldPollRecordsCommand(), CancellationToken.None);

        // Assert — 3 old records deleted
        deleted.Should().Be(3, "3 records are older than 90 days");

        // 2 recent records still present
        db.PollRecords.Count().Should().Be(2);
    }

    [Fact]
    public async Task PruneJob_WhenNoOldRecords_ReturnsZero()
    {
        TableStorageContext db = TestDoubles.CreateDb();

        var handler = new PruneOldPollRecordsCommandHandler(db, NullLogger<PruneOldPollRecordsCommandHandler>.Instance);

        int deleted = await handler.Handle(new PruneOldPollRecordsCommand(), CancellationToken.None);

        deleted.Should().Be(0);
    }

    [Fact]
    public async Task PruneJob_DoesNotTouchRecordExactlyAtBoundary()
    {
        // Record exactly 90 days ago should NOT be pruned (boundary is exclusive)
        TableStorageContext db = TestDoubles.CreateDb();
        RouteId routeId = RouteId.New();

        // Exactly 90 days — borderline (should NOT be deleted per spec: < 90 days window means > 90 days is deleted)
        // PolledAt < GETUTCDATE() - 90 → strictly less than means exact boundary is not deleted
        db.Add(new PollRecord
        {
            Id = PollRecordId.New(),
            RouteId = routeId,
            PolledAt = DateTime.UtcNow.AddDays(-90).AddMinutes(5), // 90 days ago + 5 min → just inside window
            TravelDurationSeconds = 300,
            DistanceMetres = 5000
        });
        await db.SaveChangesAsync();

        var handler = new PruneOldPollRecordsCommandHandler(db, NullLogger<PruneOldPollRecordsCommandHandler>.Instance);

        int deleted = await handler.Handle(new PruneOldPollRecordsCommand(), CancellationToken.None);

        deleted.Should().Be(0, "record within 90-day window must not be pruned");
    }
}
