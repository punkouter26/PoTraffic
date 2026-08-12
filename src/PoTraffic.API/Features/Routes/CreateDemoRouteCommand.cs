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
    // A real commute, not a placeholder pair. The coordinates are pinned rather than
    // geocoded at runtime — that is what keeps loading a demo free — and they were
    // resolved once through this app's own Google provider, so they are the same
    // values a real route for these addresses would store.
    private const string DemoOriginAddress = "4451 Telfair Blvd, Camp Springs, MD 20746";
    private const string DemoOriginCoordinates = "38.828112,-76.909925";
    private const string DemoDestinationAddress = "5325 Westbard Ave, Bethesda, MD 20816";
    private const string DemoDestinationCoordinates = "38.9611502,-77.1067288";

    /// <summary>Fixed so every account's sample data — and every screenshot of it — is identical.</summary>
    private const int HistorySeed = 20260811;

    private const int HistoryDays = 14;

    /// <summary>
    /// Measured live on this route at a quiet hour: 2,355 s over 37,921 m. Named for
    /// what it is — a light-traffic baseline the rush-hour humps build on — rather
    /// than claimed as true free-flow, which would need a reading nobody took.
    /// </summary>
    private const int QuietHourSeconds = 2_355;
    private const int DistanceMetres = 37_921;

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

            // UTC, like everything else in the polling model — but chosen so the
            // sample reads as the commute it now depicts. These addresses are in US
            // Eastern (UTC-4 in summer), so 10–12 UTC is the 06:00–08:00 run in and
            // 20–22 UTC is the 16:00–18:00 run home. The previous 6–8/16–18 would
            // have put this Maryland commute's rush hour at 2 AM local.
            foreach (int hour in (int[])[10, 11, 12, 20, 21, 22])
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

        // Peak multipliers are lower than they were for the old 21-minute placeholder
        // route: on a 39-minute drive the same factors would have produced a 74-minute
        // peak. These land around 66 min in the morning, which is the shape this
        // corridor actually has.
        // Centres are UTC: 12:00 is 08:00 Eastern, 21:00 is 17:00 Eastern.
        double morning = Hump(minuteOfDay, centre: 12 * 60, width: 50) * 0.70;
        double evening = Hump(minuteOfDay, centre: 21 * 60, width: 60) * 0.55;

        double dayFactor = at.DayOfWeek switch
        {
            DayOfWeek.Friday => 1.15,
            DayOfWeek.Thursday => 1.07,
            DayOfWeek.Monday => 1.04,
            _ => 1.00
        };

        double jitter = 0.94 + (rng.NextDouble() * 0.12);
        return QuietHourSeconds * (1 + morning + evening) * dayFactor * jitter;
    }

    /// <summary>Gaussian bump, 1.0 at <paramref name="centre"/> and falling away either side.</summary>
    private static double Hump(double minuteOfDay, double centre, double width) =>
        Math.Exp(-Math.Pow(minuteOfDay - centre, 2) / (2 * width * width));
}
