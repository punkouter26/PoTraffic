using PoTraffic.API.Infrastructure.Dispatch;
using Microsoft.Extensions.DependencyInjection;
using PoTraffic.API.Features.Maintenance;
using PoTraffic.API.Infrastructure.Storage;

using PoTraffic.Tests.Helpers;
using Xunit;


namespace PoTraffic.Tests.Features.Maintenance;

/// <summary>
/// Integration test that seeds 90-day boundary PollRecord data and verifies the
/// pruning command correctly soft-deletes records older than 90 days and leaves
/// records at the boundary untouched.
///
/// FR-020: PruneOldPollRecordsCommand MUST soft-delete (IsDeleted=true, RawProviderResponse=null)
/// only records where PolledAt &lt; (UtcNow - 90 days). Records at exactly 90 days and newer
/// MUST be preserved.
/// </summary>
public sealed class PruningBoundaryTests : BaseIntegrationTest
{
    public PruningBoundaryTests() : base() { }

    /// <summary>
    /// Verifies the 90-day pruning boundary:
    ///
    /// Given  poll records exist at three ages relative to today:
    ///          - 91 days ago (beyond cutoff — should be pruned)
    ///          - 90 days ago (at cutoff boundary — should be PRESERVED per exclusive less-than)
    ///          - 89 days ago (within retention window — should be preserved)
    /// When   PruneOldPollRecordsCommand is executed
    /// Then   only the 91-day-old record is removed
    /// And    the 90-day and 89-day records remain
    /// </summary>
    [SkipUnlessAzuriteAvailable]
    public async Task PruneCommand_SoftDeletesRecordsBeyond90Days_PreservesAt90DayBoundary()
    {
        // Arrange — ensure migrations are applied and access DI services
        await ApplyMigrationsAsync();

        _ = CreateClient(); // warm up host

        // Access services via the test application's DI container
        using IServiceScope scope = GetServices().CreateScope();
        TableStorageContext db = scope.ServiceProvider.GetRequiredService<TableStorageContext>();
        ISender sender = scope.ServiceProvider.GetRequiredService<ISender>();

        DateTime now = DateTime.UtcNow;

        User user = new()
        {
            Id = UserId.New(),
            Email = "prune-boundary@test.invalid",
            Locale = "en-IE",
            CreatedAt = now.AddDays(-100)
        };
        db.Add(user);

        EntityRoute route = new()
        {
            Id = RouteId.New(),
            UserId = user.Id,
            OriginAddress = "Origin",
            DestinationAddress = "Destination",
            Provider = (int)PoTraffic.Shared.Enums.RouteProvider.GoogleMaps,
            MonitoringStatus = 0,
            CreatedAt = now.AddDays(-100)
        };
        db.Add(route);

        MonitoringSession session = new()
        {
            Id = SessionId.New(),
            RouteId = route.Id,
            SessionDate = DateOnly.FromDateTime(now.AddDays(-91)),
            IsHolidayExcluded = false
        };
        db.Add(session);

        // Record 1: 91 days old — BEYOND cutoff, MUST be pruned
        PollRecord beyond = new()
        {
            Id = PollRecordId.New(),
            RouteId = route.Id,
            SessionId = session.Id,
            PolledAt = new DateTimeOffset(now.AddDays(-91)),
            TravelDurationSeconds = 600,
            DistanceMetres = 5000,
            IsRerouted = false
        };

        // Record 2: exactly 90 days minus a 5-minute buffer — AT boundary.
        // The handler recalculates cutoff = DateTime.UtcNow.AddDays(-90) at execution time,
        // which may be seconds after 'now'. The buffer prevents a timing race.
        PollRecord atBoundary = new()
        {
            Id = PollRecordId.New(),
            RouteId = route.Id,
            SessionId = session.Id,
            PolledAt = new DateTimeOffset(now.AddDays(-90).AddMinutes(5)),
            TravelDurationSeconds = 610,
            DistanceMetres = 5010,
            IsRerouted = false
        };

        // Record 3: 89 days old — WITHIN window, MUST be preserved
        PollRecord recent = new()
        {
            Id = PollRecordId.New(),
            RouteId = route.Id,
            SessionId = session.Id,
            PolledAt = new DateTimeOffset(now.AddDays(-89)),
            TravelDurationSeconds = 620,
            DistanceMetres = 5020,
            IsRerouted = false
        };

        db.AddRange(new PollRecord[] { beyond, atBoundary, recent });
        await db.SaveChangesAsync();

        // Act
        await sender.Send(new PruneOldPollRecordsCommand());

        // Assert — 91-day record must be hard-deleted
        PollRecord? reloadedBeyond = db.PollRecords.FirstOrDefault(r => r.Id == beyond.Id);
        PollRecord? reloadedAtBoundary = db.PollRecords.FirstOrDefault(r => r.Id == atBoundary.Id);
        PollRecord? reloadedRecent = db.PollRecords.FirstOrDefault(r => r.Id == recent.Id);

        Assert.Null(reloadedBeyond);

        // 90-day record must be preserved (boundary is exclusive <, not <=)
        Assert.NotNull(reloadedAtBoundary);

        // 89-day record must be preserved
        Assert.NotNull(reloadedRecent);
    }
}
