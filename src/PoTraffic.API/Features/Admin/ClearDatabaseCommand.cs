using PoTraffic.API.Infrastructure.Storage;



using PoTraffic.API.Infrastructure.Scheduling;

namespace PoTraffic.API.Features.Admin;

/// <summary>
/// ClearDatabaseCommand — Diagnostic utility for admins to purge all volatile/demo data.
/// Keeps only Administrator user accounts.
/// </summary>
public sealed record ClearDatabaseCommand() : IRequest<ClearDatabaseResult>;

public sealed record ClearDatabaseResult(int UsersDeleted, int RoutesDeleted, int PollsDeleted);

public sealed class ClearDatabaseHandler(
    TableStorageContext db,
    IJobScheduler scheduler,
    ILogger<ClearDatabaseHandler> logger)
    : IRequestHandler<ClearDatabaseCommand, ClearDatabaseResult>
{
    public async Task<ClearDatabaseResult> Handle(ClearDatabaseCommand request, CancellationToken ct)
    {
        int usersToDeleteCount = db.Users.Count(u => u.Role != "Administrator");
        int routesToDeleteCount = db.Routes.Count();
        int pollsToDeleteCount = db.PollRecords.Count();

        var routesWithJobs = db.Routes
            .Where(r => r.JobChainId != null)
            .Select(r => r.JobChainId!)
            .ToList();

        foreach (var jobId in routesWithJobs)
        {
            scheduler.Cancel(jobId);
        }

        db.RemoveRange(db.PollRecords.ToList());
        db.RemoveRange(db.MonitoringSessions.ToList());
        db.RemoveRange(db.MonitoringWindows.ToList());
        db.RemoveRange(db.Routes.ToList());
        db.RemoveRange(db.Users.Where(u => u.Role != "Administrator").ToList());

        logger.LogWarning("[Admin] Database Wiped: {Users} users, {Routes} routes, {Polls} polls cleared by administrative action.",
            usersToDeleteCount, routesToDeleteCount, pollsToDeleteCount);

        return new ClearDatabaseResult(usersToDeleteCount, routesToDeleteCount, pollsToDeleteCount);
    }
}
