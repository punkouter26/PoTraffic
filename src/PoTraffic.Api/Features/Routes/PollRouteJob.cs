using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PoTraffic.Api.Features.MonitoringWindows.Entities;
using PoTraffic.Api.Infrastructure.Scheduling;
using PoTraffic.Api.Infrastructure.Storage;
using PoTraffic.Shared.Constants;
using PoTraffic.Shared.Enums;

namespace PoTraffic.Api.Features.Routes;

// Chain of Responsibility pattern — each job enqueues its own successor to maintain the polling chain.
// Polls are gated to the route's active MonitoringWindow (UTC): inside the window the chain
// samples every PollIntervalMinutes; outside it the chain sleeps until the next window start.
public sealed class PollRouteJob
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IJobScheduler _scheduler;
    private readonly ILogger<PollRouteJob> _logger;

    public PollRouteJob(
        IServiceScopeFactory scopeFactory,
        IJobScheduler scheduler,
        ILogger<PollRouteJob> logger)
    {
        _scopeFactory = scopeFactory;
        _scheduler = scheduler;
        _logger = logger;
    }

    public async Task Execute(Guid routeId)
    {
        _logger.LogDebug("PollRouteJob executing for route {RouteId}", routeId);

        // Collapse a forked chain: if a crash re-ran a job that had already scheduled its
        // successor, cancel any stray pending poll job(s) for this route so exactly one
        // chain proceeds. No-op in normal operation.
        try
        {
            int collapsed = _scheduler.CancelPendingPollJobsForRoute(routeId);
            if (collapsed > 0)
                _logger.LogWarning(
                    "PollRouteJob: collapsed {Count} duplicate poll job(s) for route {RouteId}", collapsed, routeId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PollRouteJob: duplicate-collapse check failed for route {RouteId}", routeId);
        }

        using IServiceScope scope = _scopeFactory.CreateScope();
        TableStorageContext db = scope.ServiceProvider.GetRequiredService<TableStorageContext>();

        try
        {
            // Stop the chain when the route was deleted mid-poll — a soft-deleted route
            // must never keep consuming provider quota.
            EntityRoute? route = db.Routes.FirstOrDefault(r => r.Id == routeId);
            if (route is null || route.MonitoringStatus == (int)MonitoringStatus.Deleted)
            {
                _logger.LogInformation("PollRouteJob: route {RouteId} is gone or deleted — polling chain stopped", routeId);
                return;
            }

            MonitoringWindow? window = db.Windows.FirstOrDefault(w => w.RouteId == routeId && w.IsActive);
            if (window is null)
            {
                _logger.LogInformation("PollRouteJob: route {RouteId} has no active monitoring window — polling chain stopped", routeId);
                route.JobChainId = null;
                await db.SaveChangesAsync();
                return;
            }

            DateTimeOffset now = DateTimeOffset.UtcNow;
            TimeSpan nextDelay;

            if (IsWithinWindow(window, now))
            {
                if (await EnsureActiveSessionAsync(db, route, now))
                {
                    ISender sender = scope.ServiceProvider.GetRequiredService<ISender>();
                    try
                    {
                        await sender.Send(new ExecutePollCommand(routeId));
                    }
                    catch (Exception ex)
                    {
                        // Log but do not rethrow — scheduler must not retry on handler errors
                        _logger.LogError(ex, "PollRouteJob: Unhandled error for route {RouteId}", routeId);
                    }
                    nextDelay = TimeSpan.FromMinutes(QuotaConstants.PollIntervalMinutes);
                }
                else if (!TrySleepUntilNextStart(window, now, routeId, "daily quota exhausted", out nextDelay))
                {
                    route.JobChainId = null;
                    await db.SaveChangesAsync();
                    return;
                }
            }
            else if (!TrySleepUntilNextStart(window, now, routeId, "outside monitoring window", out nextDelay))
            {
                route.JobChainId = null;
                await db.SaveChangesAsync();
                return;
            }

            // Schedule next execution — Chain of Responsibility enqueues its own successor
            string nextJobId = _scheduler.Schedule(
                () => Execute(routeId),
                nextDelay);

            _logger.LogDebug(
                "PollRouteJob: Next poll for route {RouteId} scheduled as job {JobId} in {Delay}", routeId, nextJobId, nextDelay);

            route.JobChainId = nextJobId;
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            // A transient dependency failure (e.g. Table Storage blip) must not permanently
            // kill the chain. Re-arm a successor so polling resumes once storage recovers;
            // if even that fails, startup reconciliation is the backstop.
            _logger.LogError(ex, "PollRouteJob: unexpected failure for route {RouteId} — re-arming chain", routeId);
            try
            {
                string retryJobId = _scheduler.Schedule(
                    () => Execute(routeId),
                    TimeSpan.FromMinutes(QuotaConstants.PollIntervalMinutes));

                EntityRoute? route = db.Routes.FirstOrDefault(r => r.Id == routeId);
                if (route is not null)
                {
                    route.JobChainId = retryJobId;
                    await db.SaveChangesAsync();
                }
            }
            catch (Exception reArmEx)
            {
                _logger.LogError(reArmEx,
                    "PollRouteJob: failed to re-arm chain for route {RouteId} — startup reconciliation will recover it", routeId);
            }
        }
    }

    /// <summary>
    /// Computes the delay to the next window start; false when the window has no
    /// enabled days (chain should stop).
    /// </summary>
    private bool TrySleepUntilNextStart(MonitoringWindow window, DateTimeOffset now, Guid routeId, string reason, out TimeSpan delay)
    {
        DateTimeOffset? nextStart = NextWindowStart(window, now);
        if (nextStart is null)
        {
            _logger.LogInformation("PollRouteJob: route {RouteId} window has no enabled days — polling chain stopped", routeId);
            delay = default;
            return false;
        }

        _logger.LogInformation(
            "PollRouteJob: route {RouteId} {Reason} — sleeping until window start {NextStart:O}", routeId, reason, nextStart);
        delay = nextStart.Value - now;
        return true;
    }

    /// <summary>
    /// A window's daily session is normally created when the user presses Start;
    /// this creates the next day's session automatically at window start so a
    /// Mon–Fri schedule keeps sampling without manual restarts. Honours the
    /// per-user daily session quota.
    /// </summary>
    private async Task<bool> EnsureActiveSessionAsync(TableStorageContext db, EntityRoute route, DateTimeOffset nowUtc)
    {
        DateOnly today = DateOnly.FromDateTime(nowUtc.UtcDateTime.Date);
        bool hasActive = db.Sessions.Any(s =>
            s.RouteId == route.Id && s.SessionDate == today && s.State == (int)SessionState.Active);
        if (hasActive)
            return true;

        HashSet<Guid> userRouteIds = db.GetUserRouteIds(route.UserId);
        int todaySessionCount = db.Sessions.Count(s => userRouteIds.Contains(s.RouteId) && s.SessionDate == today);
        if (todaySessionCount >= QuotaConstants.DefaultDailyQuota)
        {
            _logger.LogInformation(
                "PollRouteJob: daily session quota exhausted for user {UserId} — route {RouteId} skips today",
                route.UserId, route.Id);
            return false;
        }

        db.Add(new MonitoringSession
        {
            Id = Guid.NewGuid(),
            RouteId = route.Id,
            SessionDate = today,
            State = (int)SessionState.Active
        });
        await db.SaveChangesAsync();
        _logger.LogInformation(
            "PollRouteJob: auto-created today's monitoring session for route {RouteId} at window start", route.Id);
        return true;
    }

    /// <summary>Window times are UTC; mask bit 0 = Monday … bit 6 = Sunday.</summary>
    internal static bool IsWithinWindow(MonitoringWindow window, DateTimeOffset nowUtc)
    {
        TimeOnly time = TimeOnly.FromDateTime(nowUtc.UtcDateTime);
        return IsDayEnabled(window.DaysOfWeekMask, nowUtc.UtcDateTime.DayOfWeek)
            && time >= window.StartTime
            && time < window.EndTime;
    }

    /// <summary>Next UTC instant the window opens after <paramref name="nowUtc"/>; null when no days are enabled.</summary>
    internal static DateTimeOffset? NextWindowStart(MonitoringWindow window, DateTimeOffset nowUtc)
    {
        for (int i = 0; i <= 7; i++)
        {
            DateTime day = nowUtc.UtcDateTime.Date.AddDays(i);
            if (!IsDayEnabled(window.DaysOfWeekMask, day.DayOfWeek))
                continue;

            DateTimeOffset start = new(day.Add(window.StartTime.ToTimeSpan()), TimeSpan.Zero);
            if (start > nowUtc)
                return start;
        }
        return null;
    }

    private static bool IsDayEnabled(byte mask, DayOfWeek day) =>
        (mask & (1 << (((int)day + 6) % 7))) != 0;
}
