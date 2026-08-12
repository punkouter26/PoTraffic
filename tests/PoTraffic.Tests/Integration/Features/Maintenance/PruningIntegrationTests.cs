using PoTraffic.API.Infrastructure.Dispatch;
using Microsoft.Extensions.DependencyInjection;
using PoTraffic.API.Features.Maintenance;
using PoTraffic.API.Infrastructure.Storage;

using PoTraffic.Tests.Helpers;


namespace PoTraffic.Tests.Features.Maintenance;

/// <summary>
/// Integration tests for <see cref="PruneOldPollRecordsJobHandler"/>.
/// FR-020: soft-delete only records with PolledAt &lt; today - 90 days.
/// </summary>
public sealed class PruningIntegrationTests : BaseIntegrationTest
{
    [SkipUnlessAzuriteAvailable]
    public async Task PruneJob_DeletesOldRecords_LeavesRecentRecordsUntouched()
    {
        await ApplyMigrationsAsync();
        _ = CreateClient();

        using IServiceScope scope = GetServices().CreateScope();
        TableStorageContext db = scope.ServiceProvider.GetRequiredService<TableStorageContext>();
        ISender sender = scope.ServiceProvider.GetRequiredService<ISender>();

        DateTime now = DateTime.UtcNow;

        // Seed user + route
        User user = new()
        {
            Id = UserId.New(),
            Email = "prune-test@test.invalid",
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
            Provider = 0,
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

        // 5 old records (91 days ago — should be pruned)
        List<PollRecord> oldRecords = Enumerable.Range(0, 5).Select(i => new PollRecord
        {
            Id = PollRecordId.New(),
            RouteId = route.Id,
            SessionId = session.Id,
            PolledAt = new DateTimeOffset(now.AddDays(-91).AddMinutes(i * 5)),
            TravelDurationSeconds = 600 + i,
            DistanceMetres = 5000,
            IsRerouted = false
        }).ToList();

        // 3 recent records (89 days ago — should be preserved)
        List<PollRecord> recentRecords = Enumerable.Range(0, 3).Select(i => new PollRecord
        {
            Id = PollRecordId.New(),
            RouteId = route.Id,
            SessionId = session.Id,
            PolledAt = new DateTimeOffset(now.AddDays(-89).AddMinutes(i * 5)),
            TravelDurationSeconds = 610 + i,
            DistanceMetres = 5010,
            IsRerouted = false
        }).ToList();

        db.AddRange(oldRecords);
        db.AddRange(recentRecords);
        await db.SaveChangesAsync();

        // Act
        await sender.Send(new PruneOldPollRecordsCommand());

        // Assert — old records should be hard-deleted
        foreach (PollRecord old in oldRecords)
        {
            PollRecord? reloaded = db.PollRecords.FirstOrDefault(r => r.Id == old.Id);
            Assert.Null(reloaded);
        }

        // Assert — recent records should NOT be pruned
        foreach (PollRecord recent in recentRecords)
        {
            PollRecord? reloaded = db.PollRecords.FirstOrDefault(r => r.Id == recent.Id);
            Assert.NotNull(reloaded);
        }
    }
}
