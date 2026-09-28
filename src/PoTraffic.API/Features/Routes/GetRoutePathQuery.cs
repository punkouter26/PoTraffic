using Microsoft.Extensions.Logging;
using PoTraffic.API.Infrastructure.Providers;
using PoTraffic.API.Infrastructure.Storage;
using PoTraffic.API.Infrastructure.Time;
using PoTraffic.Shared.Constants;
using PoTraffic.Shared.DTOs.Routes;
using PoTraffic.Shared.Enums;

namespace PoTraffic.API.Features.Routes;

/// <summary>
/// Everything the route map needs: the road shape, and how the newest sample compares
/// to what this route normally does on this weekday at this time of day.
/// </summary>
public sealed record GetRoutePathQuery(RouteId RouteId, UserId UserId);

public sealed class GetRoutePathQueryHandler(
    TableStorageContext db,
    ITrafficProvider provider,
    ILogger<GetRoutePathQueryHandler> logger)
{
    /// <summary>
    /// How far above (or below) the route's own typical duration each colour band starts.
    /// Ratios, not absolute minutes, so a 12-minute hop and a 70-minute haul are judged
    /// on the same scale — the same rule the heatmap uses.
    /// </summary>
    private const double ClearBelow = 0.95;
    private const double NormalBelow = 1.10;
    private const double SlowBelow = 1.25;

    /// <summary>How long a found incident stays on the map. TomTom only reports live
    /// incidents, but nothing re-checks one until the next alert, so it has to age out.</summary>
    internal static readonly TimeSpan IncidentFreshFor = TimeSpan.FromMinutes(45);

    public async Task<RoutePathDto?> Handle(GetRoutePathQuery q, CancellationToken ct)
    {
        EntityRoute? route = db.GetOwnedRoute(q.RouteId, q.UserId, excludeDeleted: true);
        if (route is null)
            return null;

        if (!TryParseCoordinates(route.OriginCoordinates, out double oLat, out double oLng) ||
            !TryParseCoordinates(route.DestinationCoordinates, out double dLat, out double dLng))
        {
            // A route always has geocoded endpoints; if these are unparseable there is
            // nothing to centre a map on, so report nothing rather than draw the Atlantic.
            logger.LogWarning("Route {RouteId} has unparseable coordinates; no map can be drawn.", q.RouteId);
            return null;
        }

        string? polyline = await EnsurePolylineAsync(route, ct);

        (int? latest, DateTimeOffset? latestAt, int? typical) = Compare(route);

        return new RoutePathDto(
            route.Id,
            polyline,
            oLat, oLng,
            dLat, dLng,
            Level(latest, typical),
            latest,
            typical,
            latestAt,
            IsApproximate: polyline is null,
            Incident: route.LastIncident is { } i && DateTimeOffset.UtcNow - i.SeenAt < IncidentFreshFor
                ? new RouteIncidentDto(i.Description, i.Lat, i.Lon, i.SeenAt)
                : null);
    }

    /// <summary>
    /// Returns the stored shape, fetching it from the provider exactly once per route.
    /// A failed fetch is remembered too, so a key without the Directions product does not
    /// trigger an outbound request on every map view forever.
    /// </summary>
    private async Task<string?> EnsurePolylineAsync(EntityRoute route, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(route.PathPolyline))
            return route.PathPolyline;

        if (route.PathUnavailableAt is not null)
            return null;

        RouteGeometry? geometry = await provider.GetRouteGeometryAsync(
            route.OriginCoordinates, route.DestinationCoordinates, ct);

        if (geometry is null)
            route.PathUnavailableAt = DateTimeOffset.UtcNow;
        else
            route.PathPolyline = geometry.EncodedPolyline;

        await db.SaveChangesAsync(ct);
        return geometry?.EncodedPolyline;
    }

    /// <summary>
    /// Newest sample, and the mean of every earlier sample in the same weekday and
    /// 15-minute slot. Same bucketing as the congestion alert, so the map's colour and
    /// the bell's message can never disagree about whether today is bad.
    /// </summary>
    private (int? Latest, DateTimeOffset? LatestAt, int? Typical) Compare(EntityRoute route)
    {
        PollRecord? newest = db.Polls
            .Where(p => p.RouteId == route.Id)
            .OrderByDescending(p => p.PolledAt)
            .FirstOrDefault();

        if (newest is null)
            return (null, null, null);

        List<int> history = db.SameSlotDurations(
            route.Id, newest.PolledAt, db.ZoneFor(route.UserId), p => p.Id != newest.Id);

        int? typical = history.Count >= QuotaConstants.BaselineMinSessionCount
            ? (int)Math.Round(history.Average())
            : null;

        return (newest.TravelDurationSeconds, newest.PolledAt, typical);
    }

    private static string Level(int? latest, int? typical)
    {
        if (latest is not > 0 || typical is not > 0)
            return "unknown";

        double ratio = (double)latest.Value / typical.Value;
        return ratio < ClearBelow ? "clear"
             : ratio < NormalBelow ? "normal"
             : ratio < SlowBelow ? "slow"
             : "heavy";
    }

    /// <summary>Parses the stored "lat,lng" pair. Invariant culture: these are provider output, not user input.</summary>
    private static bool TryParseCoordinates(string? value, out double lat, out double lng)
    {
        lat = lng = 0;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        string[] parts = value.Split(',', 2);
        return parts.Length == 2
            && double.TryParse(parts[0], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out lat)
            && double.TryParse(parts[1], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out lng);
    }
}
