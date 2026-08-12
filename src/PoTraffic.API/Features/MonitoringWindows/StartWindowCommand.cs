using System.Collections.Concurrent;

using PoTraffic.API.Infrastructure.Scheduling;
using PoTraffic.API.Infrastructure.Storage;



using Microsoft.Extensions.Logging;



using PoTraffic.API.Features.Routes;

using PoTraffic.Shared.Constants;

using PoTraffic.Shared.Enums;

namespace PoTraffic.API.Features.MonitoringWindows;

public sealed record StartWindowCommand(
    WindowId WindowId,
    UserId UserId) : IRequest<StartWindowResult>;

public sealed record StartWindowResult(
    bool IsSuccess,
    string? ErrorCode,   // RouteErrorCodes.NotFound | "QUOTA_EXCEEDED"
    int QuotaRemaining,
    SessionId? SessionId);

public sealed class StartWindowCommandHandler : IRequestHandler<StartWindowCommand, StartWindowResult>
{
    // Per-route single-flight gate. The store is a process singleton with no compound-operation
    // isolation, so without this two concurrent Starts (double-click / client retry) can both
    // observe "no session yet", both pass the quota gate, and both enqueue a poll chain —
    // duplicate paid provider calls plus an orphaned, un-cancellable chain.
    private static readonly ConcurrentDictionary<RouteId, SemaphoreSlim> s_routeGates = new();

    private static SemaphoreSlim GateFor(RouteId routeId) =>
        s_routeGates.GetOrAdd(routeId, static _ => new SemaphoreSlim(1, 1));

    private readonly TableStorageContext _db;
    private readonly IJobScheduler _scheduler;
    private readonly ILogger<StartWindowCommandHandler> _logger;

    public StartWindowCommandHandler(
        TableStorageContext db,
        IJobScheduler scheduler,
        ILogger<StartWindowCommandHandler> logger)
    {
        _db = db;
        _scheduler = scheduler;
        _logger = logger;
    }

    public async Task<StartWindowResult> Handle(StartWindowCommand cmd, CancellationToken ct)
    {
        // 1. Load window, then resolve the owning route explicitly — TableStorageContext
        // has no navigation property to back the old `w.Route.UserId` access.
        MonitoringWindow? window = _db.MonitoringWindows
            .FirstOrDefault(w => w.Id == cmd.WindowId);

        if (window is null)
            return new StartWindowResult(false, RouteErrorCodes.NotFound, 0, null);

        EntityRoute? route = _db.GetOwnedRoute(window.RouteId, cmd.UserId, excludeDeleted: true);
        if (route is null)
            return new StartWindowResult(false, RouteErrorCodes.NotFound, 0, null);

        // The sample route (#10) carries synthetic history. Arming a polling chain against it
        // would spend real provider quota and interleave measured samples with fabricated
        // ones in the same series — the user's own route is the place to start monitoring.
        if (route.IsDemo)
            return new StartWindowResult(false, RouteErrorCodes.DemoRoute, 0, null);

        DateOnly today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.Date);

        SemaphoreSlim gate = GateFor(route.Id);
        await gate.WaitAsync(ct);
        try
        {
            // The user's route IDs — used for per-user daily quota counting.
            HashSet<RouteId> userRouteIds = _db.GetUserRouteIds(cmd.UserId);

            // 2. Idempotent guard — return existing session if one already exists for this route today
            MonitoringSession? existingSession = _db.MonitoringSessions
                .FirstOrDefault(s => s.RouteId == route.Id && s.SessionDate == today);

            if (existingSession is not null)
            {
                _logger.LogInformation(
                    "Start called for window {WindowId} but session {SessionId} already exists for today — idempotent return",
                    cmd.WindowId, existingSession.Id);
                int quotaUsed = _db.MonitoringSessions
                    .Count(s => userRouteIds.Contains(s.RouteId) && s.SessionDate == today);
                return new StartWindowResult(true, null, Math.Max(0, QuotaConstants.DefaultDailyQuota - quotaUsed), existingSession.Id);
            }

            // 3. Count today's sessions for this user across all their routes
            int todaySessionCount = _db.MonitoringSessions
                .Count(s => userRouteIds.Contains(s.RouteId) && s.SessionDate == today);

            if (todaySessionCount >= QuotaConstants.DefaultDailyQuota)
            {
                _logger.LogInformation("Quota exceeded for user {UserId} on {Date}", cmd.UserId, today);
                return new StartWindowResult(false, "QUOTA_EXCEEDED", 0, null);
            }

            // 4. Create MonitoringSession (IsHolidayExcluded always false — PublicHolidays table removed S-09)
            var session = new MonitoringSession
            {
                Id = SessionId.New(),
                RouteId = route.Id,
                SessionDate = today,
                State = (int)SessionState.Active,
                IsHolidayExcluded = false,
                QuotaConsumed = 0,
                PollCount = 0
            };

            _db.Add(session);

            await _db.SaveChangesAsync(ct);

            // 6. Enqueue AFTER successful DB save — ensures no orphan job if SaveChanges fails.
            //    Cancel any prior chain first so we never leave a second, un-cancellable chain running.
            if (route.JobChainId is { } priorJobId)
                _scheduler.Cancel(priorJobId);

            PollRouteJob routeJob = new(null!, null!, null!);
            string jobId = _scheduler.Enqueue(() => routeJob.Execute(route.Id));

            // 7. Store job ID so DeleteRouteCommand can cancel it.
            route.JobChainId = jobId;
            await _db.SaveChangesAsync(ct);

            _logger.LogInformation(
                "Monitoring started for window {WindowId}, route {RouteId}, session {SessionId}, job {JobId}",
                cmd.WindowId, route.Id, session.Id, jobId);

            int remaining = QuotaConstants.DefaultDailyQuota - todaySessionCount - 1;
            return new StartWindowResult(true, null, remaining, session.Id);
        }
        finally
        {
            gate.Release();
        }
    }
}
