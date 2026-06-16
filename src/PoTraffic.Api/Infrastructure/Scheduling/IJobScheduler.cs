using System.Linq.Expressions;

namespace PoTraffic.Api.Infrastructure.Scheduling;

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
    /// Register a recurring job with a CRON expression.
    /// </summary>
    /// <param name="jobId">Stable identifier for the recurring job (replaces previous registration).</param>
    /// <param name="job">Async function to execute.</param>
    /// <param name="cronExpression">CRON expression (e.g. "0 2 * * *" for daily at 02:00 UTC).</param>
    void ScheduleRecurring(string jobId, Func<Task> job, string cronExpression);

    /// <summary>
    /// Cancel a recurring job by its stable identifier.
    /// </summary>
    void CancelRecurring(string jobId);
}
