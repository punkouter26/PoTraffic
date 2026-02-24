using Hangfire;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PoTraffic.Api.Infrastructure.Data;

using PoTraffic.Api.Features.Routes;
using PoTraffic.Shared.Constants;
using PoTraffic.Shared.Enums;

namespace PoTraffic.Api.Features.MonitoringWindows;

public sealed record StartWindowCommand(
    Guid WindowId,
    Guid UserId) : IRequest<StartWindowResult>;

public sealed record StartWindowResult(
    bool IsSuccess,
    string? ErrorCode,   // "NOT_FOUND" | "QUOTA_EXCEEDED"
    int QuotaRemaining,
    Guid? SessionId);

public sealed class StartWindowCommandHandler : IRequestHandler<StartWindowCommand, StartWindowResult>
{
    private readonly PoTrafficDbContext _db;
    private readonly IBackgroundJobClient _jobClient;
    private readonly ILogger<StartWindowCommandHandler> _logger;

    public StartWindowCommandHandler(
        PoTrafficDbContext db,
        IBackgroundJobClient jobClient,
        ILogger<StartWindowCommandHandler> logger)
    {
        _db = db;
        _jobClient = jobClient;
        _logger = logger;
    }

    public async Task<StartWindowResult> Handle(StartWindowCommand cmd, CancellationToken ct)
    {
        // 1. Load window + route, verify ownership
        MonitoringWindow? window = await _db.MonitoringWindows
            .Include(w => w.Route)
            .FirstOrDefaultAsync(w => w.Id == cmd.WindowId
                && w.Route.UserId == cmd.UserId
                && w.Route.MonitoringStatus != (int)MonitoringStatus.Deleted, ct);

        if (window is null)
            return new StartWindowResult(false, "NOT_FOUND", 0, null);

        DateOnly today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.Date);

        // 2. Idempotent guard — return existing session if one already exists for this route today
        MonitoringSession? existingSession = await _db.MonitoringSessions
            .FirstOrDefaultAsync(s => s.RouteId == window.Route.Id && s.SessionDate == today, ct);

        if (existingSession is not null)
        {
            _logger.LogInformation(
                "Start called for window {WindowId} but session {SessionId} already exists for today — idempotent return",
                cmd.WindowId, existingSession.Id);
            int quotaUsed = await _db.MonitoringSessions
                .CountAsync(s => s.Route.UserId == cmd.UserId && s.SessionDate == today, ct);
            return new StartWindowResult(true, null, Math.Max(0, QuotaConstants.DefaultDailyQuota - quotaUsed), existingSession.Id);
        }

        // 3. Count today's sessions for this user across all their routes
        int todaySessionCount = await _db.MonitoringSessions
            .CountAsync(s => s.Route.UserId == cmd.UserId && s.SessionDate == today, ct);

        if (todaySessionCount >= QuotaConstants.DefaultDailyQuota)
        {
            _logger.LogInformation("Quota exceeded for user {UserId} on {Date}", cmd.UserId, today);
            return new StartWindowResult(false, "QUOTA_EXCEEDED", 0, null);
        }

        // 4. Create MonitoringSession (IsHolidayExcluded always false — PublicHolidays table removed S-09)
        var session = new MonitoringSession
        {
            RouteId = window.Route.Id,
            SessionDate = today,
            State = (int)SessionState.Active,
            IsHolidayExcluded = false,
            QuotaConsumed = 0,
            PollCount = 0
        };

        _db.MonitoringSessions.Add(session);

        // 5. Schedule PollRouteJob immediately
        string jobId = _jobClient.Enqueue<PollRouteJob>(j => j.Execute(window.Route.Id));

        // 6. Store job ID in route.HangfireJobChainId
        window.Route.HangfireJobChainId = jobId;

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Monitoring started for window {WindowId}, route {RouteId}, session {SessionId}, job {JobId}",
            cmd.WindowId, window.Route.Id, session.Id, jobId);

        int remaining = QuotaConstants.DefaultDailyQuota - todaySessionCount - 1;
        return new StartWindowResult(true, null, remaining, session.Id);
    }
}
