using Azure.Data.Tables;
using FluentAssertions;
using PoTraffic.Api.Infrastructure.Storage;
using PoTraffic.Tests.Helpers;
using PoTraffic.Tests.Infrastructure;

namespace PoTraffic.Tests.Features.Storage;

/// <summary>
/// The core durability guarantee: everything written through
/// <see cref="TableStorageContext"/> must survive a process restart —
/// modelled here as a brand-new context hydrating from the same Azurite store.
/// </summary>
public sealed class PersistenceRoundTripTests
{
    private static async Task<ITableStore> CreateStoreAsync()
    {
        string connectionString = await AzuriteTestContainer.GetConnectionStringAsync();
        return new AzureTableStore(new TableServiceClient(connectionString));
    }

    [SkipUnlessAzuriteAvailable]
    public async Task AddsUpdatesAndDeletes_SurviveRestart()
    {
        ITableStore store = await CreateStoreAsync();

        Guid userId = Guid.NewGuid();
        Guid routeId = Guid.NewGuid();
        Guid pollId = Guid.NewGuid();
        string email = $"roundtrip-{userId:N}@potraffic.dev";

        // ── Process 1 — create, mutate in place, delete ──────────────────────
        var ctx1 = new TableStorageContext(store);
        await ctx1.HydrateAsync();

        ctx1.Add(new User { Id = userId, Email = email, Locale = "en-US", CreatedAt = DateTimeOffset.UtcNow });
        var route = new EntityRoute
        {
            Id = routeId,
            UserId = userId,
            OriginAddress = "A",
            OriginCoordinates = "0,0",
            DestinationAddress = "B",
            DestinationCoordinates = "1,1",
            CreatedAt = DateTimeOffset.UtcNow
        };
        ctx1.Add(route);
        ctx1.Add(new PollRecord { Id = pollId, RouteId = routeId, PolledAt = DateTimeOffset.UtcNow, TravelDurationSeconds = 600 });
        await ctx1.SaveChangesAsync();

        // In-place mutation with no explicit Update() call — must still persist.
        route.OriginAddress = "A-updated";
        await ctx1.SaveChangesAsync();

        // Delete the poll — must not resurrect after restart.
        PollRecord poll = ctx1.Polls.Single(p => p.Id == pollId);
        ctx1.Remove(poll);
        await ctx1.SaveChangesAsync();

        // ── Process 2 — fresh context, same store ────────────────────────────
        var ctx2 = new TableStorageContext(store);
        await ctx2.HydrateAsync();

        ctx2.Users.SingleOrDefault(u => u.Id == userId).Should().NotBeNull("users must survive a restart");
        EntityRoute? rehydrated = ctx2.Routes.SingleOrDefault(r => r.Id == routeId);
        rehydrated.Should().NotBeNull("routes must survive a restart");
        rehydrated!.OriginAddress.Should().Be("A-updated", "in-place mutations must be persisted by snapshot diffing");
        ctx2.Polls.Any(p => p.Id == pollId).Should().BeFalse("deleted rows must not resurrect");
    }

    [SkipUnlessAzuriteAvailable]
    public async Task Hydration_RelinksRouteWindows()
    {
        ITableStore store = await CreateStoreAsync();

        Guid routeId = Guid.NewGuid();
        var ctx1 = new TableStorageContext(store);
        await ctx1.HydrateAsync();

        var route = new EntityRoute
        {
            Id = routeId,
            UserId = Guid.NewGuid(),
            OriginAddress = "A",
            OriginCoordinates = "0,0",
            DestinationAddress = "B",
            DestinationCoordinates = "1,1"
        };
        route.Windows.Add(new PoTraffic.Api.Features.MonitoringWindows.Entities.MonitoringWindow
        {
            Id = Guid.NewGuid(),
            RouteId = routeId,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(10, 0),
            DaysOfWeekMask = 31,
            IsActive = true
        });
        ctx1.Add(route);
        await ctx1.SaveChangesAsync();

        var ctx2 = new TableStorageContext(store);
        await ctx2.HydrateAsync();

        EntityRoute rehydrated = ctx2.Routes.Single(r => r.Id == routeId);
        rehydrated.Windows.Should().HaveCount(1, "hydration must relink navigation collections handlers read");
        rehydrated.Windows.Single().StartTime.Should().Be(new TimeOnly(8, 0));
    }
}
