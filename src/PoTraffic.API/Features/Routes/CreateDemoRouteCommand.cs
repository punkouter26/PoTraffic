using Microsoft.Extensions.Logging;
using PoTraffic.API.Infrastructure.Storage;
using PoTraffic.Shared.DTOs.Routes;
using PoTraffic.Shared.Enums;

namespace PoTraffic.API.Features.Routes;

/// <summary>
/// Seeds the caller's own account with a single sample route carrying two weeks of
/// synthetic history (#10).
///
/// <para>
/// An empty dashboard is a dead end: the product has nothing to show until a real route
/// has been monitored for days. This gives a new account something to read immediately —
/// baseline, optimal departure, weekday comparison and the weekly grid all populate at once.
/// </para>
///
/// <para>
/// Nothing here calls a traffic provider. The addresses are pinned to known coordinates
/// rather than geocoded, the route is created paused with no monitoring window, and it is
/// flagged <see cref="EntityRoute.IsDemo"/> so the UI can label it and the billing paths
/// can refuse it. Loading a demo must never cost money or quota.
/// </para>
/// </summary>
public sealed record CreateDemoRouteCommand(UserId UserId) : IRequest<CreateRouteResult>;

public sealed class CreateDemoRouteCommandHandler(
    TableStorageContext db,
    ILogger<CreateDemoRouteCommandHandler> logger)
    : IRequestHandler<CreateDemoRouteCommand, CreateRouteResult>
{
    private const string DemoOriginAddress = "1600 Amphitheatre Pkwy, Mountain View, CA (sample)";
    private const string DemoOriginCoordinates = "37.4220,-122.0841";
    private const string DemoDestinationAddress = "1 Apple Park Way, Cupertino, CA (sample)";
    private const string DemoDestinationCoordinates = "37.3349,-122.0090";

    /// <summary>Fixed so every account's sample data — and every screenshot of it — is identical.</summary>
    private const int HistorySeed = 20260811;

    private const int HistoryDays = 14;
    private const int FreeFlowSeconds = 1260;   // 21 min with nothing in the way
    private const int DistanceMetres = 19_000;

    public async Task<CreateRouteResult> Handle(CreateDemoRouteCommand cmd, CancellationToken ct)
    {
        // Idempotent: a second click hands back the route the first one made rather than
        // stacking up duplicate sample data.
        EntityRoute? existing = db.Routes.FirstOrDefault(r =>
            r.UserId == cmd.UserId
            && r.IsDemo
            && r.MonitoringStatus != (int)MonitoringStatus.Deleted);

        if (existing is not null)
        {
            logger.LogInformation("Demo route {RouteId} already exists for user {UserId}", existing.Id, cmd.UserId);
            return new CreateRouteResult(true, null, CreateRouteCommandHandler.MapToDto(existing));
        }

        var route = new EntityRoute
        {
            Id = RouteId.New(),
            UserId = cmd.UserId,
            OriginAddress = DemoOriginAddress,
            OriginCoordinates = DemoOriginCoordinates,
            DestinationAddress = DemoDestinationAddress,
            DestinationCoordinates = DemoDestinationCoordinates,
            Provider = (int)RouteProvider.GoogleMaps,
            // Paused, and deliberately without a monitoring window: the polling chain arms
            // off an active window, so there is no path from here to a billed provider call.
            MonitoringStatus = (int)MonitoringStatus.Paused,
            IsDemo = true,
            CreatedAt = DateTimeOffset.UtcNow
        };

        db.Add(route);

        List<PollRecord> history = BuildHistory(route.Id, DateTimeOffset.UtcNow);
        foreach (PollRecord record in history)
            db.Add(record);

        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Demo route {RouteId} created for user {UserId} with {PollCount} synthetic samples",
            route.Id, cmd.UserId, history.Count);

        return new CreateRouteResult(true, null, CreateRouteCommandHandler.MapToDto(route));
    }

    /// <summary>
    /// Two weeks of weekday commute samples, morning and evening, at the same 15-minute
    /// cadence a real monitoring window would produce.
    /// </summary>
    internal static List<PollRecord> BuildHistory(RouteId routeId, DateTimeOffset nowUtc)
    {
        var rng = new Random(HistorySeed);
        List<PollRecord> records = [];

        for (int dayOffset = HistoryDays; dayOffset >= 0; dayOffset--)
        {
            DateTime day = nowUtc.UtcDateTime.Date.AddDays(-dayOffset);
            if (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
                continue;

            foreach (int hour in (int[])[6, 7, 8, 16, 17, 18])
            {
                for (int minute = 0; minute < 60; minute += 15)
                {
                    var polledAt = new DateTimeOffset(day.AddHours(hour).AddMinutes(minute), TimeSpan.Zero);

                    // Sample data that runs into the future would be visibly wrong on the
                    // "past 24h" views the moment the route is opened.
                    if (polledAt > nowUtc)
                        continue;

                    // One in twenty-five commutes goes badly — enough to give the trend
                    // chart reroute markers and the grid a volatile cell or two.
                    bool anomaly = rng.Next(25) == 0;
                    double seconds = DurationSeconds(polledAt, rng) * (anomaly ? 1.45 : 1.0);

                    records.Add(new PollRecord
                    {
                        Id = PollRecordId.New(),
                        RouteId = routeId,
                        // No session: sessions are the unit the daily quota counts, and
                        // synthetic history must not spend a real account's allowance.
                        SessionId = null,
                        PolledAt = polledAt,
                        TravelDurationSeconds = (int)Math.Round(seconds),
                        DistanceMetres = anomaly ? (int)(DistanceMetres * 1.15) : DistanceMetres,
                        IsRerouted = anomaly,
                        RawProviderResponse = "{ \"status\": \"OK\", \"demo\": true }"
                    });
                }
            }
        }

        return records;
    }

    /// <summary>
    /// Free-flow time inflated by two rush-hour humps (08:00 and 17:00 UTC) and a
    /// day-of-week factor, then jittered. The shape is what makes the demo worth looking
    /// at — flat noise would render an empty-looking heatmap and a meaningless baseline.
    /// </summary>
    private static double DurationSeconds(DateTimeOffset at, Random rng)
    {
        double minuteOfDay = (at.Hour * 60) + at.Minute;
        double morning = Hump(minuteOfDay, centre: 8 * 60, width: 50) * 0.90;
        double evening = Hump(minuteOfDay, centre: 17 * 60, width: 60) * 0.70;

        double dayFactor = at.DayOfWeek switch
        {
            DayOfWeek.Friday => 1.15,
            DayOfWeek.Thursday => 1.07,
            DayOfWeek.Monday => 1.04,
            _ => 1.00
        };

        double jitter = 0.94 + (rng.NextDouble() * 0.12);
        return FreeFlowSeconds * (1 + morning + evening) * dayFactor * jitter;
    }

    /// <summary>Gaussian bump, 1.0 at <paramref name="centre"/> and falling away either side.</summary>
    private static double Hump(double minuteOfDay, double centre, double width) =>
        Math.Exp(-Math.Pow(minuteOfDay - centre, 2) / (2 * width * width));
}
