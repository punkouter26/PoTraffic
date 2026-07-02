using PoTraffic.Api.Infrastructure.Dispatch;
using Microsoft.Extensions.DependencyInjection;
using PoTraffic.Api.Features.Maintenance;
using PoTraffic.Api.Infrastructure.Storage;

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
            Id = Guid.NewGuid(),
            Email = "prune-test@test.invalid",
            Locale = "en-IE",
            CreatedAt = now.AddDays(-100)
        };
        db.Add(user);

        EntityRoute route = new()
        {
            Id = Guid.NewGuid(),
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
            Id = Guid.NewGuid(),
            RouteId = route.Id,
            SessionDate = DateOnly.FromDateTime(now.AddDays(-91)),
            IsHolidayExcluded = false
        };
        db.Add(session);

        // 5 old records (91 days ago — should be pruned)
        List<PollRecord> oldRecords = Enumerable.Range(0, 5).Select(i => new PollRecord
        {
            Id = Guid.NewGuid(),
            RouteId = route.Id,
            SessionId = session.Id,
            PolledAt = new DateTimeOffset(now.AddDays(-91).AddMinutes(i * 5)),
            TravelDurationSeconds = 600 + i,
            DistanceMetres = 5000,
            IsRerouted = false,
            RawProviderResponse = "{\"status\":\"ok\"}",
            IsDeleted = false
        }).ToList();

        // 3 recent records (89 days ago — should be preserved)
        List<PollRecord> recentRecords = Enumerable.Range(0, 3).Select(i => new PollRecord
        {
            Id = Guid.NewGuid(),
            RouteId = route.Id,
            SessionId = session.Id,
            PolledAt = new DateTimeOffset(now.AddDays(-89).AddMinutes(i * 5)),
            TravelDurationSeconds = 610 + i,
            DistanceMetres = 5010,
            IsRerouted = false,
            RawProviderResponse = "{\"status\":\"ok\"}",
            IsDeleted = false
        }).ToList();

        db.AddRange(oldRecords);
        db.AddRange(recentRecords);
        await db.SaveChangesAsync();

        // Act
        await sender.Send(new PruneOldPollRecordsCommand());

        // Assert — old records should be soft-deleted
        foreach (PollRecord old in oldRecords)
        {
            PollRecord? reloaded = db.PollRecords.FirstOrDefault(r => r.Id == old.Id);
            Assert.NotNull(reloaded);
            Assert.True(reloaded!.IsDeleted, "91-day record should be soft-deleted");
            Assert.Null(reloaded.RawProviderResponse);
        }

        // Assert — recent records should NOT be pruned
        foreach (PollRecord recent in recentRecords)
        {
            PollRecord? reloaded = db.PollRecords.FirstOrDefault(r => r.Id == recent.Id);
            Assert.NotNull(reloaded);
            Assert.False(reloaded!.IsDeleted, "89-day record should NOT be pruned");
            Assert.NotNull(reloaded.RawProviderResponse);
        }
    }
}
