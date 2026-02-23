using MediatR;
using Microsoft.EntityFrameworkCore;
using PoTraffic.Api.Infrastructure.Data;

using Hangfire;

namespace PoTraffic.Api.Features.Admin;

/// <summary>
/// ClearDatabaseCommand — Diagnostic utility for admins to purge all volatile/demo data.
/// Keeps only Administrator user accounts.
/// </summary>
public sealed record ClearDatabaseCommand() : IRequest<ClearDatabaseResult>;

public sealed record ClearDatabaseResult(int UsersDeleted, int RoutesDeleted, int PollsDeleted);

public sealed class ClearDatabaseHandler(
    PoTrafficDbContext db,
    ILogger<ClearDatabaseHandler> logger) 
    : IRequestHandler<ClearDatabaseCommand, ClearDatabaseResult>
{
    public async Task<ClearDatabaseResult> Handle(ClearDatabaseCommand request, CancellationToken ct)
    {
        // 1. Snapshot counts for the return DTO
        int usersToDeleteCount = await db.Users.CountAsync(u => u.Role != "Administrator", ct);
        int routesToDeleteCount = await db.Routes.CountAsync(ct);
        int pollsToDeleteCount = await db.PollRecords.CountAsync(ct);

        // 2. Identify all routes to clear Hangfire job chains for them
        // Although the user might not care about dangling jobs if the DB is cleared,
        // it's cleaner to remove them from server queue.
        var activeRoutesWithJobs = await db.Routes
            .Where(r => r.HangfireJobChainId != null)
            .Select(r => r.HangfireJobChainId)
            .ToListAsync(ct);

        foreach (var jobId in activeRoutesWithJobs)
        {
            if (!string.IsNullOrEmpty(jobId))
            {
                BackgroundJob.Delete(jobId);
            }
        }

        // 3. Clear data tables. 
        // EF Core with Cascade handles everything if we remove non-admin users.
        // However, to ensure we catch routes/polls even if they somehow detached, we empty them too.
        
        // ExecuteDelete is more efficient for large clear-downs in EF Core 7+ (which we are on NET 10)
        await db.PollRecords.ExecuteDeleteAsync(ct);
        await db.MonitoringSessions.ExecuteDeleteAsync(ct);
        await db.MonitoringWindows.ExecuteDeleteAsync(ct);
        await db.Routes.ExecuteDeleteAsync(ct);
        
        // Final step: clear non-admin users
        await db.Users.Where(u => u.Role != "Administrator").ExecuteDeleteAsync(ct);

        logger.LogWarning("[Admin] Database Wiped: {Users} users, {Routes} routes, {Polls} polls cleared by administrative action.", 
            usersToDeleteCount, routesToDeleteCount, pollsToDeleteCount);

        return new ClearDatabaseResult(usersToDeleteCount, routesToDeleteCount, pollsToDeleteCount);
    }
}
