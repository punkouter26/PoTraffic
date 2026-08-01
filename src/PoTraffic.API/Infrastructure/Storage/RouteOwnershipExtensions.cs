using PoTraffic.Shared.Enums;

namespace PoTraffic.API.Infrastructure.Storage;

/// <summary>
/// Route-ownership lookups against <see cref="TableStorageContext"/>.
///
/// <para>
/// The store has no navigation properties (the EF Core <c>PoTrafficDbContext</c> was
/// replaced by an in-memory/Table-Storage backend), so the natural <c>entity.Route.UserId</c>
/// access NREs. These helpers centralise the "resolve a route by id and verify the caller
/// owns it" boundary so the IDOR check lives in one place instead of being hand-copied into
/// every handler — change the ownership rule here and every call site follows.
/// </para>
/// </summary>
public static class RouteOwnershipExtensions
{
    /// <summary>
    /// Returns the route with <paramref name="routeId"/> if it is owned by
    /// <paramref name="userId"/>, otherwise <c>null</c>. When <paramref name="excludeDeleted"/>
    /// is set, soft-deleted routes are treated as not found.
    /// </summary>
    public static EntityRoute? GetOwnedRoute(
        this TableStorageContext db,
        RouteId routeId,
        UserId userId,
        bool excludeDeleted = false)
    {
        return db.Routes.FirstOrDefault(r =>
            r.Id == routeId
            && r.UserId == userId
            && (!excludeDeleted || r.MonitoringStatus != (int)MonitoringStatus.Deleted));
    }

    /// <summary>
    /// True when <paramref name="routeId"/> exists and is owned by <paramref name="userId"/>.
    /// Cheaper than <see cref="GetOwnedRoute"/> when the route entity itself isn't needed.
    /// </summary>
    public static bool OwnsRoute(
        this TableStorageContext db,
        RouteId routeId,
        UserId userId,
        bool excludeDeleted = false)
    {
        return db.Routes.Any(r =>
            r.Id == routeId
            && r.UserId == userId
            && (!excludeDeleted || r.MonitoringStatus != (int)MonitoringStatus.Deleted));
    }

    /// <summary>
    /// The set of route ids owned by <paramref name="userId"/> — used to scope
    /// per-user aggregates (e.g. daily quota counts) without a navigation property.
    /// </summary>
    public static HashSet<RouteId> GetUserRouteIds(this TableStorageContext db, UserId userId)
        => db.Routes.Where(r => r.UserId == userId).Select(r => r.Id).ToHashSet();
}
