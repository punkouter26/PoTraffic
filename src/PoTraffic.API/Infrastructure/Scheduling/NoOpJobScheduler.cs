using System.Linq.Expressions;

namespace PoTraffic.API.Infrastructure.Scheduling;

/// <summary>
/// Fallback no-op scheduler used when Azurite is unavailable.
/// All scheduling calls are silently ignored.
/// </summary>
internal sealed class NoOpJobScheduler : IJobScheduler
{
    public string Enqueue(Expression<Func<Task>> job) => "noop";
    public string Schedule(Expression<Func<Task>> job, TimeSpan delay) => "noop";
    public void Cancel(string jobId) { }
    public int CancelPendingPollJobsForRoute(RouteId routeId) => 0;
    public void ScheduleRecurring(string jobId, Func<Task> job, string cronExpression) { }
    public void CancelRecurring(string jobId) { }
}
