using Microsoft.Extensions.Logging;
using PoTraffic.Api.Features.Alerts.Entities;
using PoTraffic.Api.Features.MonitoringWindows.Entities;
using PoTraffic.Api.Features.Routes.Entities;
using PoTraffic.Api.Infrastructure.Storage;
using PoTraffic.Shared.Constants;
using PoTraffic.Shared.DTOs.Alerts;

namespace PoTraffic.Api.Features.Alerts;

/// <summary>
/// Raises a proactive alert (#1) when a freshly-recorded poll crosses the route's baseline
/// for the same UTC day-of-week and 15-minute slot (mean + σ), or when a reroute is detected.
/// De-duplicated per session so a congested commute produces one alert, not one per poll.
/// Runs inside the poll's scope; push delivery failures never break polling.
/// </summary>
public sealed class AlertEvaluator(
    TableStorageContext db,
    IPushNotifier push,
    ILogger<AlertEvaluator> logger)
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
                Id = Guid.NewGuid(),
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
                await push.SendAsync(a.UserId, ToDto(a), ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Push notify failed for alert {AlertId}", a.Id);
            }
        }
    }

    private bool TryBuildCongestionAlert(EntityRoute route, PollRecord record, MonitoringSession session, out Alert? alert)
    {
        alert = null;
        DayOfWeek dow = record.PolledAt.DayOfWeek;
        int bucket = (record.PolledAt.Hour * 4) + (record.PolledAt.Minute / 15);

        // Baseline from prior sessions only (exclude this session so a spike isn't compared to itself).
        List<int> hist = db.Polls
            .Where(p => p.RouteId == route.Id && !p.IsDeleted && p.SessionId != session.Id
                && p.PolledAt.DayOfWeek == dow
                && (p.PolledAt.Hour * 4) + (p.PolledAt.Minute / 15) == bucket)
            .Select(p => p.TravelDurationSeconds)
            .ToList();

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
            Id = Guid.NewGuid(),
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
