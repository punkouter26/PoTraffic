using Microsoft.Extensions.Logging;
using PoTraffic.API.Features.Alerts;
using PoTraffic.API.Features.MonitoringWindows;
using PoTraffic.API.Features.Routes;
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
public sealed class AlertEvaluator(TableStorageContext db, IPushNotifier push, ILogger<AlertEvaluator> logger)
{
    public async Task EvaluateAsync(EntityRoute route, PollRecord record, MonitoringSession session, CancellationToken ct)
    {
        List<Alert> raised = [];

        if (TryBuildCongestionAlert(route, record, session, out Alert? congestion))
            raised.Add(congestion!);

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

        foreach (Alert a in raised)
            db.Add(a);
        await db.SaveChangesAsync(ct);

        foreach (Alert a in raised)
        {
            try
            {
                await push.SendAsync(a.UserId, new PushPayload(
                    a.Kind == "Reroute" ? "Route changed" : "Heavier traffic than usual",
                    a.Message,
                    $"/routes/{a.RouteId}",
                    $"alert-{a.RouteId}"), AlertPushTtl, ct);
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

    internal static AlertDto ToDto(Alert a) =>
        new(a.Id, a.RouteId, a.Kind, a.Message, a.TravelSeconds, a.BaselineSeconds, a.CreatedAt, a.IsRead);
}
