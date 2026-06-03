using MediatR;
using PoTraffic.Api.Infrastructure.Storage;



using Hangfire;

namespace PoTraffic.Api.Features.Admin;

/// <summary>
/// ClearDatabaseCommand — Diagnostic utility for admins to purge all volatile/demo data.
/// Keeps only Administrator user accounts.
/// </summary>
public sealed record ClearDatabaseCommand() : IRequest<ClearDatabaseResult>;

public sealed record ClearDatabaseResult(int UsersDeleted, int RoutesDeleted, int PollsDeleted);

public sealed class ClearDatabaseHandler(
    TableStorageContext db,
    ILogger<ClearDatabaseHandler> logger)
    : IRequestHandler<ClearDatabaseCommand, ClearDatabaseResult>
{
    public async Task<ClearDatabaseResult> Handle(ClearDatabaseCommand request, CancellationToken ct)
    {
        // 1. Snapshot counts for the return DTO
        int usersToDeleteCount = await db.Users.Count(u => u.Role != "Administrator");
        int routesToDeleteCount = await db.Routes.Count();
        int pollsToDeleteCount = await db.PollRecords.Count();

        // 2. Identify all routes to clear Hangfire job chains for them
        // Although the user might not care about dangling jobs if the DB is cleared,
        // it's cleaner to remove them from server queue.
        var activeRoutesWithJobs = await db.Routes
            .Where(r => r.HangfireJobChainId != null)
            .Select(r => r.HangfireJobChainId)
            .ToList();

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
        await db.PollRecords.ToList().ForEach(e => _db.Remove(e))(ct);
        await db.MonitoringSessions.ToList().ForEach(e => _db.Remove(e))(ct);
        await db.MonitoringWindows.ToList().ForEach(e => _db.Remove(e))(ct);
        await db.Routes.ToList().ForEach(e => _db.Remove(e))(ct);

        // Final step: clear non-admin users
        await db.Users.Where(u => u.Role != "Administrator").ToList().ForEach(e => _db.Remove(e))(ct);

        logger.LogWarning("[Admin] Database Wiped: {Users} users, {Routes} routes, {Polls} polls cleared by administrative action.",
            usersToDeleteCount, routesToDeleteCount, pollsToDeleteCount);

        return new ClearDatabaseResult(usersToDeleteCount, routesToDeleteCount, pollsToDeleteCount);
    }
}
