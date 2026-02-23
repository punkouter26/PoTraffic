using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PoTraffic.Api.Features.Maintenance;
using PoTraffic.Api.Infrastructure.Data;

using PoTraffic.IntegrationTests.Helpers;


namespace PoTraffic.IntegrationTests.Features.Maintenance;

/// <summary>
/// Integration tests for <see cref="PruneOldPollRecordsJobHandler"/>.
/// FR-020: soft-delete only records with PolledAt &lt; today - 90 days.
/// </summary>
public sealed class PruningIntegrationTests : BaseIntegrationTest
{
    [SkipUnlessDockerAvailable]
    public async Task PruneJob_DeletesOldRecords_LeavesRecentRecordsUntouched()
    {
        await ApplyMigrationsAsync();
        _ = CreateClient();

        using IServiceScope scope = GetServices().CreateScope();
        PoTrafficDbContext db = scope.ServiceProvider.GetRequiredService<PoTrafficDbContext>();
        ISender sender = scope.ServiceProvider.GetRequiredService<ISender>();

        DateTime now = DateTime.UtcNow;

        // Seed user + route
        User user = new()
        {
            Id = Guid.NewGuid(),
            Email = "prune-test@test.invalid",
            PasswordHash = "hash",
            Locale = "en-IE",
            CreatedAt = now.AddDays(-100)
        };
        db.Users.Add(user);

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
        db.Routes.Add(route);

        MonitoringSession session = new()
        {
            Id = Guid.NewGuid(),
            RouteId = route.Id,
            SessionDate = DateOnly.FromDateTime(now.AddDays(-91)),
            IsHolidayExcluded = false
        };
        db.MonitoringSessions.Add(session);

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

        db.PollRecords.AddRange(oldRecords);
        db.PollRecords.AddRange(recentRecords);
        await db.SaveChangesAsync();

        // Act
        await sender.Send(new PruneOldPollRecordsCommand());

        // Assert
        db.ChangeTracker.Clear();

        foreach (PollRecord old in oldRecords)
        {
            PollRecord? reloaded = await db.PollRecords
                .IgnoreQueryFilters()
                .SingleOrDefaultAsync(p => p.Id == old.Id);
            Assert.NotNull(reloaded);
            Assert.True(reloaded!.IsDeleted, $"Record {old.Id} (91 days old) should be soft-deleted");
            Assert.Null(reloaded.RawProviderResponse);
        }

        foreach (PollRecord recent in recentRecords)
        {
            PollRecord? reloaded = await db.PollRecords
                .IgnoreQueryFilters()
                .SingleOrDefaultAsync(p => p.Id == recent.Id);
            Assert.NotNull(reloaded);
            Assert.False(reloaded!.IsDeleted, $"Record {recent.Id} (89 days old) should NOT be pruned");
            Assert.NotNull(reloaded.RawProviderResponse);
        }
    }
}
