using System.Linq.Expressions;

namespace PoTraffic.API.Infrastructure.Scheduling;

/// <summary>
/// Abstraction for scheduling background jobs.
/// Backed by Azure Table Storage (Azurite locally, Azure in prod) for durability.
/// </summary>
public interface IJobScheduler
{
    /// <summary>
    /// Enqueue a job for immediate execution (fire-and-forget).
    /// </summary>
    /// <param name="job">Lambda expression calling the job method (e.g. j => j.Execute(id)).</param>
    /// <returns>The unique job ID.</returns>
    string Enqueue(Expression<Func<Task>> job);

    /// <summary>
    /// Schedule a job for delayed execution.
    /// </summary>
    /// <param name="job">Lambda expression calling the job method.</param>
    /// <param name="delay">Time from now until execution.</param>
    /// <returns>The unique job ID.</returns>
    string Schedule(Expression<Func<Task>> job, TimeSpan delay);

    /// <summary>
    /// Cancel a pending or scheduled job by ID.
    /// </summary>
    void Cancel(string jobId);

    /// <summary>
    /// Cancels every *pending* one-shot poll job targeting <paramref name="routeId"/>.
    /// Called at the start of a poll execution to collapse a forked chain: if a crash
    /// re-ran a job that had already scheduled its successor, two pending successors
    /// exist — this cancels the stragglers so exactly one chain survives. A no-op in
    /// normal operation (the only live poll job for the route is the one running now).
    /// </summary>
    /// <returns>The number of duplicate pending jobs cancelled.</returns>
    int CancelPendingPollJobsForRoute(RouteId routeId);

    /// <summary>
    /// Register a job to run once a day at a fixed UTC time.
    /// </summary>
    /// <remarks>
    /// Deliberately a <see cref="TimeOnly"/> rather than a CRON string: daily-at-a-time is the
    /// only recurrence this app schedules, and expressing it directly removes a general CRON
    /// parser (fields, ranges, lists, steps, plus a minute-by-minute scan up to a year out)
    /// that nothing exercised and no test covered.
    /// </remarks>
    /// <param name="jobId">Stable identifier for the recurring job (replaces previous registration).</param>
    /// <param name="job">Async function to execute.</param>
    /// <param name="dailyAtUtc">Time of day, UTC, at which the job fires.</param>
    void ScheduleRecurring(string jobId, Func<Task> job, TimeOnly dailyAtUtc);
}
