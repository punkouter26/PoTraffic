namespace PoTraffic.Shared.DTOs.Routes;

/// <summary>
/// The drawable shape of a route, plus how today's latest sample compares to what
/// this route normally does at this time.
///
/// <para>
/// The shape (<see cref="EncodedPolyline"/>) is the road path as Google returns it —
/// an encoded polyline, fetched once and stored on the route, because the roads
/// between two addresses do not change between probes. When the provider cannot
/// supply one, <see cref="IsApproximate"/> is true and the client draws a straight
/// line between the two endpoints instead of nothing.
/// </para>
///
/// <para>
/// The colour comes from THIS app's own samples, not from a live traffic feed.
/// Colouring per road segment would need a per-segment traffic product on every
/// map view; the whole point of PoTraffic is that it already knows what this
/// commute normally takes, so the line is tinted by
/// <see cref="LatestDurationSeconds"/> against <see cref="TypicalDurationSeconds"/>.
/// That costs nothing extra and is the measurement the user actually trusts.
/// </para>
/// </summary>
/// <param name="TrafficLevel">
/// One of <c>unknown</c>, <c>clear</c>, <c>normal</c>, <c>slow</c>, <c>heavy</c>.
/// <c>unknown</c> means there is no baseline for this weekday and time slot yet —
/// distinct from "normal", which is a real finding.
/// </param>
/// <param name="TypicalDurationSeconds">
/// Mean of prior samples for the same weekday and 15-minute slot, or null when
/// too few exist to say anything.
/// </param>
public sealed record RoutePathDto(
    RouteId RouteId,
    string? EncodedPolyline,
    double OriginLat,
    double OriginLng,
    double DestinationLat,
    double DestinationLng,
    string TrafficLevel,
    int? LatestDurationSeconds,
    int? TypicalDurationSeconds,
    DateTimeOffset? LatestProbeAt,
    bool IsApproximate);
