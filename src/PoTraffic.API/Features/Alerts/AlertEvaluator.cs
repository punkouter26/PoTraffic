using Microsoft.Extensions.Logging;
using PoTraffic.API.Features.Alerts;
using PoTraffic.API.Features.MonitoringWindows;
using PoTraffic.API.Features.Routes;
using PoTraffic.API.Infrastructure.Providers;
using PoTraffic.API.Infrastructure.Storage;
using PoTraffic.API.Infrastructure.Time;
using PoTraffic.Shared.Constants;
using PoTraffic.Shared.DTOs.Alerts;

namespace PoTraffic.API.Features.Alerts;

/// <summary>
/// Raises a proactive alert (#1) when a freshly-recorded poll crosses the route's baseline
/// for the same local day-of-week and 15-minute slot (mean + σ), or when a reroute is detected.
/// De-duplicated per session so a congested commute produces one alert, not one per poll.
/// Runs inside the poll's scope; push delivery failures never break polling.
/// </summary>
public sealed class AlertEvaluator(
    TableStorageContext db,
    IPushNotifier push,
    IIncidentProvider incidents,
    ILogger<AlertEvaluator> logger)
{
    public async Task EvaluateAsync(EntityRoute route, PollRecord record, MonitoringSession session, CancellationToken ct)
    {
        List<Alert> raised = [];

        if (TryBuildCongestionAlert(route, record, session, out Alert? congestion))
            raised.Add(congestion!);

        if (TryBuildLeaveNowAlert(route, record, session, out Alert? leave))
            raised.Add(leave!);

        if (record.IsRerouted
            && !db.Alerts.Any(a => a.SessionId == session.Id && a.Kind == "Reroute"))
        {
            raised.Add(new Alert
            {
                Id = AlertId.New(),
                UserId = route.UserId,
                RouteId = route.Id,
                SessionId = session.Id,
                Kind = "Reroute",
                Message = "Your route was rerouted — the usual path may be blocked.",
                TravelSeconds = record.TravelDurationSeconds,
                BaselineSeconds = 0,
                CreatedAt = record.PolledAt,
            });
        }

        if (raised.Count == 0)
            return;

        // Say why, when we can: one incident lookup per alert-raising poll, never per poll.
        if (raised.Any(a => a.Kind != LeaveNowKind) && !string.IsNullOrEmpty(route.PathPolyline))
        {
            try
            {
                if (await incidents.DescribeWorstOnPathAsync(route.PathPolyline, ct) is { } cause)
                {
                    foreach (Alert a in raised.Where(a => a.Kind != LeaveNowKind))
                        a.Message += $" Likely cause: {cause}.";
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Incident lookup failed for route {RouteId}", route.Id);
            }
        }

        foreach (Alert a in raised)
            db.Add(a);
        await db.SaveChangesAsync(ct);

        foreach (Alert a in raised)
        {
            try
            {
                string title = a.Kind switch
                {
                    "Reroute" => "Route changed",
                    LeaveNowKind => "Time to leave",
                    _ => "Heavier traffic than usual",
                };
                if (!string.IsNullOrEmpty(route.Name))
                    title = $"{route.Name}: {title}";
                await push.SendAsync(a.UserId, new PushPayload(
                    title, a.Message, $"/routes/{a.RouteId}", $"{a.Kind}-{a.RouteId}"), AlertPushTtl, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Push delivery failed for alert {AlertId}", a.Id);
            }
        }
    }

    /// <summary>A congestion warning an hour late is noise, so the push service may drop it after that.</summary>
    private static readonly TimeSpan AlertPushTtl = TimeSpan.FromHours(1);

    private bool TryBuildCongestionAlert(EntityRoute route, PollRecord record, MonitoringSession session, out Alert? alert)
    {
        alert = null;
        // Baseline from prior sessions only (exclude this session so a spike isn't compared to itself).
        List<int> hist = db.SameSlotDurations(
            route.Id, record.PolledAt, db.ZoneFor(route.UserId), p => p.SessionId != session.Id);

        if (hist.Count < QuotaConstants.BaselineMinSessionCount)
            return false;

        double mean = hist.Average();
        double sd = Math.Sqrt(hist.Sum(v => Math.Pow(v - mean, 2)) / (hist.Count - 1));
        if (record.TravelDurationSeconds <= mean + sd)
            return false;

        if (db.Alerts.Any(a => a.SessionId == session.Id && a.Kind == "Congestion"))
            return false;

        int overMin = (int)Math.Round((record.TravelDurationSeconds - mean) / 60.0);
        alert = new Alert
        {
            Id = AlertId.New(),
            UserId = route.UserId,
            RouteId = route.Id,
            SessionId = session.Id,
            Kind = "Congestion",
            Message = $"Heavier traffic than usual — about {overMin} min above your typical {Math.Round(mean / 60.0):F0} min.",
            TravelSeconds = record.TravelDurationSeconds,
            BaselineSeconds = (int)Math.Round(mean),
            CreatedAt = record.PolledAt,
        };
        return true;
    }

    internal const string LeaveNowKind = "LeaveNow";

    /// <summary>How long before the latest safe departure the nudge fires. The next sample can
    /// be up to 15 minutes away (adaptive cadence), so a shorter lead could arrive too late.</summary>
    internal static readonly TimeSpan LeaveNowLead = TimeSpan.FromMinutes(15);

    /// <summary>Slack on top of the live trip time, so "leave now" isn't "arrive at the last second".</summary>
    internal static readonly TimeSpan LeaveNowMargin = TimeSpan.FromMinutes(5);

    /// <summary>
    /// "Leave in 8 min" once per session for a route with an arrive-by target: the latest
    /// safe departure is the target minus the trip time measured right now (plus a margin),
    /// so a slow morning moves the nudge earlier and a clear one lets the user wait.
    /// </summary>
    private bool TryBuildLeaveNowAlert(EntityRoute route, PollRecord record, MonitoringSession session, out Alert? alert)
    {
        alert = null;
        if (route.ArriveBy is not { } arriveBy
            || db.Alerts.Any(a => a.SessionId == session.Id && a.Kind == LeaveNowKind))
            return false;

        DateTimeOffset nowLocal = record.PolledAt.ToLocal(db.ZoneFor(route.UserId));
        string? message = LeaveNowMessage(nowLocal.DateTime, arriveBy, TimeSpan.FromSeconds(record.TravelDurationSeconds));
        if (message is null)
            return false;

        alert = new Alert
        {
            Id = AlertId.New(),
            UserId = route.UserId,
            RouteId = route.Id,
            SessionId = session.Id,
            Kind = LeaveNowKind,
            Message = message,
            TravelSeconds = record.TravelDurationSeconds,
            BaselineSeconds = 0,
            CreatedAt = record.PolledAt,
        };
        return true;
    }

    /// <summary>The nudge text, or null when it is not yet time (or the target has passed).</summary>
    internal static string? LeaveNowMessage(DateTime nowLocal, TimeOnly arriveBy, TimeSpan trip)
    {
        DateTime target = nowLocal.Date + arriveBy.ToTimeSpan();
        if (nowLocal >= target)
            return null;

        DateTime leaveAt = target - trip - LeaveNowMargin;
        TimeSpan untilLeave = leaveAt - nowLocal;
        if (untilLeave > LeaveNowLead)
            return null;

        int tripMin = (int)Math.Round(trip.TotalMinutes);
        string arrive = arriveBy.ToString("HH:mm");
        if (untilLeave > TimeSpan.Zero)
            return $"Leave in {(int)Math.Ceiling(untilLeave.TotalMinutes)} min to arrive by {arrive} — the drive is {tripMin} min right now.";

        int lateMin = (int)Math.Round((nowLocal + trip - target).TotalMinutes);
        return lateMin > 0
            ? $"Leave now — at {tripMin} min you'd arrive about {lateMin} min after {arrive}."
            : $"Leave now to arrive by {arrive} — the drive is {tripMin} min right now.";
    }

    internal static AlertDto ToDto(Alert a) =>
        new(a.Id, a.RouteId, a.Kind, a.Message, a.TravelSeconds, a.BaselineSeconds, a.CreatedAt, a.IsRead);
}
