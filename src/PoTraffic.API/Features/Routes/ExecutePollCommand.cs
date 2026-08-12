using PoTraffic.API.Infrastructure.Storage;

using Microsoft.Extensions.DependencyInjection;

using Microsoft.Extensions.Logging;



using PoTraffic.API.Infrastructure.Providers;

using PoTraffic.Shared.Constants;

using PoTraffic.Shared.Enums;

namespace PoTraffic.API.Features.Routes;

public sealed record ExecutePollCommand(RouteId RouteId) : IRequest<bool>;

public sealed class ExecutePollCommandHandler(
    TableStorageContext db,
    ITrafficProviderFactory providerFactory,
    Alerts.AlertEvaluator alertEvaluator,
    ILogger<ExecutePollCommandHandler> logger) : IRequestHandler<ExecutePollCommand, bool>
{
    /// <summary>Two polls closer together than this are treated as the same logical poll
    /// (a re-executed job after a crash/requeue); the second is suppressed. Well below the
    /// minutes-apart real poll interval, so it never suppresses a legitimate poll.</summary>
    private static readonly TimeSpan DedupWindow = TimeSpan.FromSeconds(30);

    public async Task<bool> Handle(ExecutePollCommand cmd, CancellationToken ct)
    {
        // 1. Load Route + active MonitoringSession for today
        EntityRoute? route = db.Routes
            .FirstOrDefault(r => r.Id == cmd.RouteId
                && r.MonitoringStatus != (int)MonitoringStatus.Deleted);

        if (route is null)
        {
            logger.LogWarning("ExecutePollCommand: Route {RouteId} not found or deleted", cmd.RouteId);
            return false;
        }

        DateOnly today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.Date);

        MonitoringSession? session = db.MonitoringSessions
            .FirstOrDefault(s => s.RouteId == cmd.RouteId
                && s.SessionDate == today
                && s.State == (int)SessionState.Active);

        // 2. If no active session, log and return false
        if (session is null)
        {
            logger.LogInformation("ExecutePollCommand: No active session for route {RouteId} on {Date}",
                cmd.RouteId, today);
            return false;
        }

        // 2b. Idempotency guard against a re-executed job (crash between the poll and its
        // MarkCompleted requeues the job → it would poll again). Polls are spaced minutes
        // apart, so a poll landing within DedupWindow of the last one is a duplicate: skip
        // it so we neither charge the provider twice nor write a duplicate PollRecord.
        if (session.LastPollAt is { } last
            && DateTimeOffset.UtcNow - last < DedupWindow)
        {
            logger.LogInformation(
                "ExecutePollCommand: duplicate poll for route {RouteId} suppressed — last poll {Seconds:F0}s ago",
                cmd.RouteId, (DateTimeOffset.UtcNow - last).TotalSeconds);
            return false;
        }

        // 3. Resolve provider via factory (resolves keyed DI lookup)
        ITrafficProvider provider = providerFactory.GetProvider((RouteProvider)route.Provider);

        TravelResult? travelResult;

        try
        {
            // 4. Call provider — catch all exceptions; scheduler must not retry on provider errors
            travelResult = await provider.GetTravelTimeAsync(
                route.OriginCoordinates,
                route.DestinationCoordinates,
                ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "ExecutePollCommand: Provider error for route {RouteId} — poll skipped", cmd.RouteId);
            return false;
        }

        if (travelResult is null)
        {
            logger.LogWarning("ExecutePollCommand: Provider returned null for route {RouteId}", cmd.RouteId);
            return false;
        }

        // 5. Create PollRecord
        var record = new PollRecord
        {
            Id = PollRecordId.New(),
            RouteId = cmd.RouteId,
            SessionId = session.Id,
            PolledAt = DateTimeOffset.UtcNow,
            TravelDurationSeconds = travelResult.DurationSeconds,
            DistanceMetres = travelResult.DistanceMetres
        };

        // 6. Reroute detection. Only the single most recent prior record is needed alongside
        // the median, so this takes a linear MaxBy rather than sorting the whole session.
        List<PollRecord> priorRecords = [.. db.PollRecords
            .Where(p => p.SessionId == session.Id)];

        if (priorRecords.Count >= 2)
        {
            PollRecord mostRecentPrior = priorRecords.MaxBy(p => p.PolledAt)!;

            // Calculate session median distance from all prior records
            double medianDistance = CalculateMedian(priorRecords.Select(p => (double)p.DistanceMetres).ToList());
            double threshold = medianDistance * (1.0 + QuotaConstants.RerouteDistanceThresholdPercent / 100.0);

            bool currentElevated = record.DistanceMetres >= threshold;
            bool priorElevated = mostRecentPrior.DistanceMetres >= threshold;

            if (currentElevated && priorElevated)
            {
                record.IsRerouted = true;
                logger.LogInformation(
                    "Reroute detected for route {RouteId}: current={Current}m, prior={Prior}m, median={Median}m",
                    cmd.RouteId, record.DistanceMetres, mostRecentPrior.DistanceMetres, medianDistance);
            }
        }

        db.Add(record);

        // 7. Update session statistics
        session.LastPollAt = record.PolledAt;
        session.PollCount += 1;
        session.FirstPollAt ??= record.PolledAt;

        // 8. Save
        await db.SaveChangesAsync(ct);

        // 9. Proactive alert evaluation (#1) — never let it break the poll chain.
        try
        {
            await alertEvaluator.EvaluateAsync(route, record, session, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "ExecutePollCommand: alert evaluation failed for route {RouteId}", cmd.RouteId);
        }

        return true;
    }

    /// <summary>Computes the median of a list of doubles. Exposed public for test reuse.</summary>
    public static double CalculateMedian(List<double> values)
    {
        if (values.Count == 0) return 0;
        List<double> sorted = [.. values.OrderBy(v => v)];
        int mid = sorted.Count / 2;
        return sorted.Count % 2 == 0
            ? (sorted[mid - 1] + sorted[mid]) / 2.0
            : sorted[mid];
    }
}
