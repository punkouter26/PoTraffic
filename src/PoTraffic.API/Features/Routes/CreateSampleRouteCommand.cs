using System.Text;
using FluentValidation;
using Microsoft.Extensions.Logging;
using PoTraffic.API.Features.MonitoringWindows;
using PoTraffic.API.Infrastructure.Providers;
using PoTraffic.API.Infrastructure.Storage;
using PoTraffic.Shared.DTOs.Routes;
using PoTraffic.Shared.Enums;

namespace PoTraffic.API.Features.Routes;

/// <summary>
/// Seeds the demo route (#10). <paramref name="UtcOffsetMinutes"/> is the caller's offset
/// from UTC, so the generated commute lands on the user's morning rather than on UTC's —
/// the server has no other way to know, and a demo that shows a 3am commute reads as broken.
/// </summary>
public sealed record CreateSampleRouteCommand(UserId UserId, int UtcOffsetMinutes)
    : IRequest<RouteDto>;

public sealed class CreateSampleRouteValidator : AbstractValidator<CreateSampleRouteCommand>
{
    public CreateSampleRouteValidator()
    {
        // Real offsets span UTC-12:00 to UTC+14:00. Anything outside that is a client bug,
        // and unclamped it would shift the generated history off the calendar entirely.
        RuleFor(x => x.UtcOffsetMinutes)
            .InclusiveBetween(-12 * 60, 14 * 60)
            .WithMessage("UTC offset must be between -720 and 840 minutes.");
    }
}

/// <summary>
/// Builds a route whose history was generated rather than polled, so a new account has
/// something to look at immediately instead of waiting days for its first baseline.
///
/// <para>
/// Nothing here calls a provider. The coordinates are fixed, the road shape is synthesised,
/// and <c>PathUnavailableAt</c> is set so the map never asks a provider for geometry either.
/// Together with <c>IsSample</c> (which keeps the route out of quota and cost reporting) and
/// the absence of an active window (which keeps the scheduler away from it), the demo costs
/// exactly nothing.
/// </para>
/// </summary>
public sealed class CreateSampleRouteCommandHandler(
    TableStorageContext db,
    ILogger<CreateSampleRouteCommandHandler> logger)
    : IRequestHandler<CreateSampleRouteCommand, RouteDto>
{
    // A real Santa Monica → Downtown LA commute: ~24km, and congested enough in the morning
    // that the generated history has something to say.
    private const string OriginAddress = "Santa Monica Pier, Santa Monica, CA";
    private const string DestinationAddress = "5th & Grand, Downtown Los Angeles, CA";
    private static readonly (double Lat, double Lon) Origin = (34.0094, -118.4973);
    private static readonly (double Lat, double Lon) Destination = (34.0505, -118.2551);

    private const int HistoryDays = 21;
    private const int PollEveryMinutes = 10;
    private static readonly TimeOnly LocalWindowStart = new(7, 0);
    private static readonly TimeOnly LocalWindowEnd = new(9, 0);

    /// <summary>Free-flow drive time, before any rush-hour, weekday or weather effect.</summary>
    private const double FreeFlowSeconds = 22 * 60;
    private const int BaseDistanceMetres = 24_000;

    public async Task<RouteDto> Handle(CreateSampleRouteCommand cmd, CancellationToken ct)
    {
        // Idempotent: the dashboard offers this from an empty state that a double-tap can
        // easily submit twice, and two demo routes is a worse outcome than a 200 with the
        // one that already exists.
        EntityRoute? existing = db.Routes.FirstOrDefault(r =>
            r.UserId == cmd.UserId
            && r.IsSample
            && r.MonitoringStatus != (int)MonitoringStatus.Deleted);

        if (existing is not null)
        {
            logger.LogInformation("Sample route already exists for user {UserId} — returning it", cmd.UserId);
            return CreateRouteCommandHandler.MapToDto(existing);
        }

        TimeSpan offset = TimeSpan.FromMinutes(cmd.UtcOffsetMinutes);
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;

        var route = new EntityRoute
        {
            Id = RouteId.New(),
            UserId = cmd.UserId,
            OriginAddress = OriginAddress,
            OriginCoordinates = FormattableString.Invariant($"{Origin.Lat:F6},{Origin.Lon:F6}"),
            DestinationAddress = DestinationAddress,
            DestinationCoordinates = FormattableString.Invariant($"{Destination.Lat:F6},{Destination.Lon:F6}"),
            Provider = (int)RouteProvider.GoogleMaps,
            // Paused, not Active: a sample route must never enter the polling chain.
            MonitoringStatus = (int)MonitoringStatus.Paused,
            CreatedAt = nowUtc.AddDays(-HistoryDays),
            IsSample = true,
            PathPolyline = SyntheticPolyline(),
        };

        // The window is inactive — it exists so the route page can show a schedule, not so
        // the scheduler can act on it. Times are stored UTC, converted back from the user's
        // local commute hours.
        route.Windows.Add(new MonitoringWindow
        {
            Id = WindowId.New(),
            RouteId = route.Id,
            StartTime = ToUtc(LocalWindowStart, offset),
            EndTime = ToUtc(LocalWindowEnd, offset),
            DaysOfWeekMask = 0x1F,
            IsActive = false,
            CreatedAt = route.CreatedAt,
        });

        db.Add(route);

        // Seeded from the user id so the same account regenerates the same demo — a
        // reproducible screenshot beats a slightly different chart every time.
        var random = new Random(cmd.UserId.Value.GetHashCode());
        int polls = GenerateHistory(route, random, offset, nowUtc);

        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Sample route {RouteId} seeded for user {UserId} with {PollCount} synthetic polls",
            route.Id, cmd.UserId, polls);

        return CreateRouteCommandHandler.MapToDto(route);
    }

    /// <summary>
    /// Writes one completed session per past weekday, each holding a morning's worth of
    /// samples. Returns the number of poll records created.
    /// </summary>
    private int GenerateHistory(EntityRoute route, Random random, TimeSpan offset, DateTimeOffset nowUtc)
    {
        int pollCount = 0;

        // Two days in the history get an incident — a crash-shaped spike with a detour. The
        // volatility, alerts and heatmap views are all about outliers, and a history of
        // nothing but well-behaved noise leaves them looking broken.
        int firstIncident = random.Next(2, 8);
        int secondIncident = random.Next(9, HistoryDays - 1);

        // Down to 0, not 1: the route page leads with today's samples, and a demo whose
        // history stops at yesterday opens on "Still learning this route" — the exact empty
        // state the sample exists to avoid. Today is partial, ending at the current moment.
        for (int daysAgo = HistoryDays; daysAgo >= 0; daysAgo--)
        {
            DateTimeOffset localDay = nowUtc.ToOffset(offset).AddDays(-daysAgo);
            if (localDay.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
                continue;

            WeatherObservation weather = WeatherForDay(random);
            bool isIncidentDay = daysAgo == firstIncident || daysAgo == secondIncident;

            DateTimeOffset windowStart = new(
                localDay.Year, localDay.Month, localDay.Day,
                LocalWindowStart.Hour, LocalWindowStart.Minute, 0, offset);

            var session = new MonitoringSession
            {
                Id = SessionId.New(),
                RouteId = route.Id,
                SessionDate = DateOnly.FromDateTime(windowStart.UtcDateTime),
                State = (int)SessionState.Completed,
            };

            var minutesInWindow = (int)(LocalWindowEnd - LocalWindowStart).TotalMinutes;
            for (int minute = 0; minute <= minutesInWindow; minute += PollEveryMinutes)
            {
                DateTimeOffset polledAt = windowStart.AddMinutes(minute);

                // Never write a sample in the future. On the current day this ends the
                // morning wherever "now" falls — mid-window if the user is looking at this
                // during their commute, and not at all if they are looking before 07:00.
                if (polledAt > nowUtc)
                    break;

                // The incident runs from partway through the window to its end, so the day
                // shows a normal start and then a sharp, sustained climb.
                bool inIncident = isIncidentDay && minute >= minutesInWindow / 2;

                double seconds = FreeFlowSeconds
                    * RushHourFactor(LocalWindowStart.AddMinutes(minute))
                    * WeekdayFactor(localDay.DayOfWeek)
                    * WeatherFactor(weather.Condition)
                    * (inIncident ? 1.55 : 1.0)
                    * (1 + ((random.NextDouble() - 0.5) * 0.12));

                var record = new PollRecord
                {
                    Id = PollRecordId.New(),
                    RouteId = route.Id,
                    SessionId = session.Id,
                    PolledAt = polledAt.ToUniversalTime(),
                    TravelDurationSeconds = (int)Math.Round(seconds),
                    DistanceMetres = inIncident
                        ? (int)(BaseDistanceMetres * 1.22)
                        : BaseDistanceMetres + random.Next(-250, 250),
                    IsRerouted = inIncident,
                    WeatherCondition = weather.Condition,
                    TemperatureC = weather.TemperatureC,
                    PrecipitationMm = weather.PrecipitationMm,
                };

                db.Add(record);
                pollCount++;

                session.PollCount++;
                session.FirstPollAt ??= record.PolledAt;
                session.LastPollAt = record.PolledAt;
            }

            // A session with no samples is a row that means nothing — it happens on the
            // current day when the user opens this before the window starts.
            if (session.PollCount > 0)
                db.Add(session);
        }

        return pollCount;
    }

    /// <summary>
    /// Congestion through the morning: light at 07:00, peaking just after 08:00, easing by
    /// 09:00. A flat multiplier would make the optimal-departure view meaningless.
    /// </summary>
    private static double RushHourFactor(TimeOnly localTime)
    {
        double hours = localTime.ToTimeSpan().TotalHours;
        const double peakHour = 8.25;
        const double spread = 1.15;
        double distance = (hours - peakHour) / spread;
        return 1.0 + (0.55 * Math.Exp(-distance * distance));
    }

    /// <summary>Midweek is the worst of it; Monday and Friday are measurably lighter.</summary>
    private static double WeekdayFactor(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => 0.96,
        DayOfWeek.Friday => 1.04,
        _ => 1.0,
    };

    private static double WeatherFactor(string condition) => condition switch
    {
        WeatherConditions.Clear => 1.0,
        WeatherConditions.Cloudy => 1.01,
        WeatherConditions.Fog => 1.09,
        WeatherConditions.Rain => 1.14,
        WeatherConditions.Snow => 1.28,
        WeatherConditions.Storm => 1.22,
        _ => 1.0,
    };

    /// <summary>
    /// One condition for the whole morning — weather does not flicker between buckets every
    /// ten minutes, and a per-sample roll would leave every condition with a scattering of
    /// samples at every time of day, which is exactly the shape that makes the impact
    /// analysis look like noise.
    /// </summary>
    private static WeatherObservation WeatherForDay(Random random)
    {
        // Roughly a temperate coastal spring: mostly dry, rain a fifth of the time, the
        // dramatic buckets rare enough to stay believable.
        int roll = random.Next(100);
        (string condition, int code) = roll switch
        {
            < 45 => (WeatherConditions.Clear, 0),
            < 72 => (WeatherConditions.Cloudy, 3),
            < 80 => (WeatherConditions.Fog, 45),
            < 95 => (WeatherConditions.Rain, 63),
            _ => (WeatherConditions.Storm, 95),
        };

        return new WeatherObservation(
            Condition: condition,
            TemperatureC: Math.Round(8 + (random.NextDouble() * 14), 1),
            PrecipitationMm: condition is WeatherConditions.Rain or WeatherConditions.Storm
                ? Math.Round(0.4 + (random.NextDouble() * 6), 1)
                : 0,
            WeatherCode: code);
    }

    /// <summary>
    /// The UTC time-of-day for a local one. Wraps rather than clamps: a 07:00 commute at
    /// UTC+9 is 22:00 UTC the previous day, and the window model stores a time of day, not
    /// an instant.
    /// </summary>
    private static TimeOnly ToUtc(TimeOnly local, TimeSpan offset)
    {
        const long ticksPerDay = TimeSpan.TicksPerDay;
        long ticks = ((local.ToTimeSpan() - offset).Ticks % ticksPerDay + ticksPerDay) % ticksPerDay;
        return TimeOnly.FromTimeSpan(TimeSpan.FromTicks(ticks));
    }

    /// <summary>
    /// A gently curved line between the two endpoints, encoded with Google's polyline
    /// algorithm so the client's existing decoder draws it like any other route shape.
    /// It is not a real road, which is why this route is labelled a sample everywhere it
    /// appears — the alternative is a provider call, and the whole point of the demo is
    /// that it costs nothing.
    /// </summary>
    private static string SyntheticPolyline()
    {
        const int points = 24;
        var coords = new List<(double Lat, double Lon)>(points);

        for (int i = 0; i < points; i++)
        {
            double t = i / (double)(points - 1);
            double lat = Origin.Lat + ((Destination.Lat - Origin.Lat) * t);
            double lon = Origin.Lon + ((Destination.Lon - Origin.Lon) * t);

            // One shallow arc plus a small ripple, so it reads as a road rather than a ruler.
            double bow = Math.Sin(t * Math.PI) * 0.018;
            double ripple = Math.Sin(t * Math.PI * 5) * 0.0016;
            coords.Add((lat + bow + ripple, lon + (ripple * 0.6)));
        }

        return EncodePolyline(coords);
    }

    /// <summary>Google's encoded polyline algorithm, precision 5 — what the client decodes.</summary>
    internal static string EncodePolyline(IReadOnlyList<(double Lat, double Lon)> coordinates)
    {
        var sb = new StringBuilder();
        int previousLat = 0;
        int previousLon = 0;

        foreach ((double lat, double lon) in coordinates)
        {
            int scaledLat = (int)Math.Round(lat * 1e5);
            int scaledLon = (int)Math.Round(lon * 1e5);

            AppendValue(sb, scaledLat - previousLat);
            AppendValue(sb, scaledLon - previousLon);

            previousLat = scaledLat;
            previousLon = scaledLon;
        }

        return sb.ToString();

        static void AppendValue(StringBuilder sb, int value)
        {
            int shifted = value < 0 ? ~(value << 1) : value << 1;
            while (shifted >= 0x20)
            {
                sb.Append((char)((0x20 | (shifted & 0x1f)) + 63));
                shifted >>= 5;
            }
            sb.Append((char)(shifted + 63));
        }
    }
}
