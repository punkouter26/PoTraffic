using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PoTraffic.API.Features.Routes;
using PoTraffic.API.Infrastructure.Storage;
using PoTraffic.Shared.Enums;

namespace PoTraffic.API.Infrastructure.Scheduling;

/// <summary>
/// BackgroundService that ticks every second, queries Azurite for due jobs,
/// and executes them within a DI scope.
/// </summary>
public sealed class BackgroundSchedulerService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly TableStorageJobScheduler? _tableScheduler;
    private readonly ILogger<BackgroundSchedulerService> _logger;
    private static readonly ActivitySource s_activitySource = new("PoTraffic.Scheduler");

    /// <summary>
    /// Thread-safe, process-wide snapshot of the scheduler's last tick, surfaced on the
    /// admin Diagnostics "Connection Health" view so local-dev breakage is visible.
    /// </summary>
    public static SchedulerTickStatus LastTick { get; private set; } = SchedulerTickStatus.NotYetRun;

    public BackgroundSchedulerService(
        IServiceProvider serviceProvider,
        IJobScheduler scheduler,
        ILogger<BackgroundSchedulerService> logger)
    {
        _serviceProvider = serviceProvider;
        _tableScheduler = scheduler as TableStorageJobScheduler;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("BackgroundSchedulerService starting");

        // Crash recovery: jobs left in "Running" by a previous process are requeued
        // so a restart never strands a poll chain.
        if (_tableScheduler is not null)
        {
            try
            {
                int requeued = _tableScheduler.RequeueStaleRunningJobs();
                if (requeued > 0)
                    _logger.LogWarning("Requeued {Count} stale running job(s) from a previous run", requeued);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Stale-job recovery failed — continuing; jobs may need manual requeue");
            }

            // Reconciliation: re-arm any monitored route whose poll chain died (e.g. a
            // transient-storage exception left it with no successor) so polling is
            // self-healing across restarts, not only across in-process failures.
            try
            {
                ReconcilePollChains();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Poll-chain reconciliation failed — continuing");
            }
        }

        using PeriodicTimer timer = new(TimeSpan.FromSeconds(1));

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await ProcessJobs(stoppingToken);
                    LastTick = new SchedulerTickStatus(DateTimeOffset.UtcNow, Succeeded: true, Error: null);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LastTick = new SchedulerTickStatus(DateTimeOffset.UtcNow, Succeeded: false, Error: ex.Message);
                    _logger.LogWarning(ex, "BackgroundSchedulerService tick failed — will retry next tick");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("BackgroundSchedulerService stopped");
        }
    }

    /// <summary>
    /// Re-arms poll chains for monitored routes that have no live poll job. A route is
    /// considered "monitored" only if it already has a <c>JobChainId</c> (the user pressed
    /// Start), so never-started routes are left alone. A sleeping chain has a Pending
    /// successor and is therefore skipped; only genuinely dead chains are re-armed.
    /// </summary>
    private void ReconcilePollChains()
    {
        if (_tableScheduler is null)
            return;

        using IServiceScope scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TableStorageContext>();

        HashSet<RouteId> live = _tableScheduler.GetRouteIdsWithLivePollJobs();

        List<EntityRoute> monitored = db.Routes
            .Where(r => r.JobChainId != null
                && r.MonitoringStatus != (int)MonitoringStatus.Deleted)
            .ToList();

        int rearmed = 0;
        foreach (EntityRoute route in monitored)
        {
            if (live.Contains(route.Id))
                continue;
            if (!db.Windows.Any(w => w.RouteId == route.Id && w.IsActive))
                continue;

            var job = new PollRouteJob(null!, null!, null!);
            string jobId = _tableScheduler.Enqueue(() => job.Execute(route.Id));
            route.JobChainId = jobId;
            rearmed++;
        }

        if (rearmed > 0)
        {
            db.SaveChangesAsync().GetAwaiter().GetResult();
            _logger.LogWarning("Reconciled {Count} dead poll chain(s) at startup", rearmed);
        }
    }

    private async Task ProcessJobs(CancellationToken ct)
    {
        if (_tableScheduler is null)
            return;

        await RunDueJobs("One-shot", _tableScheduler.GetDueOneShotJobs(), _tableScheduler.MarkCompleted, ct);
        await RunDueJobs("Recurring", _tableScheduler.GetDueRecurringJobs(), _tableScheduler.MarkRecurringCompleted, ct);
    }

    /// <summary>
    /// Runs a batch of due jobs. One-shot and recurring differ only in how a success is
    /// recorded, so <paramref name="markCompleted"/> is the sole variation point.
    /// </summary>
    private async Task RunDueJobs(
        string kind,
        IReadOnlyList<ScheduledJobEntity> jobs,
        Action<ScheduledJobEntity> markCompleted,
        CancellationToken ct)
    {
        foreach (ScheduledJobEntity job in jobs)
        {
            if (ct.IsCancellationRequested) break;

            using Activity? activity = s_activitySource.StartActivity(
                $"Job.{job.TypeName.Split('.').Last()}.{job.MethodName}");

            activity?.SetTag("job.id", job.RowKey);
            activity?.SetTag("job.type", job.TypeName);
            activity?.SetTag("job.method", job.MethodName);
            activity?.SetTag("job.daily_at", job.DailyAtUtc);

            _tableScheduler!.MarkRunning(job);

            try
            {
                await InvokeJobMethod(job, ct);
                markCompleted(job);
                _logger.LogDebug("{Kind} job {JobId} completed, next fire at {FireAt}",
                    kind, job.RowKey, job.FireAt);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "{Kind} job {JobId} failed", kind, job.RowKey);
                _tableScheduler.MarkFailed(job, ex.Message);
            }
        }
    }

    private async Task InvokeJobMethod(ScheduledJobEntity job, CancellationToken ct)
    {
        // Check if this is a recurring job with a stored function reference
        if (job.PartitionKey == "Recurring" &&
            TableStorageJobScheduler.RecurringJobFunctions.TryGetValue(job.RowKey, out Func<Task>? func))
        {
            await func().WaitAsync(ct);
            return;
        }

        Type? jobType = Type.GetType(job.TypeName);
        if (jobType is null)
            throw new InvalidOperationException($"Job type not found: {job.TypeName}");

        MethodInfo? method = jobType.GetMethod(job.MethodName,
            BindingFlags.Public | BindingFlags.Instance);
        if (method is null)
            throw new InvalidOperationException(
                $"Method not found: {job.TypeName}.{job.MethodName}");

        // Create a DI scope so job dependencies use normal scoped lifetimes.
        await using AsyncServiceScope scope = _serviceProvider.CreateAsyncScope();
        object jobInstance = scope.ServiceProvider.GetRequiredService(jobType);

        object?[] args = DeserializeArgs(job.ArgsJson, method.GetParameters());

        // IsAssignableFrom covers Task and Task<T> alike (the result of a Task<T> job is
        // discarded); anything else is a sync method and has already run by the time
        // Invoke returns.
        if (!typeof(Task).IsAssignableFrom(method.ReturnType))
        {
            method.Invoke(jobInstance, args);
            return;
        }

        await ((Task)method.Invoke(jobInstance, args)!).WaitAsync(ct);
    }

    private static object?[] DeserializeArgs(string argsJson, ParameterInfo[] parameters)
    {
        if (string.IsNullOrWhiteSpace(argsJson) || argsJson == "[]")
            return [];

        using JsonDocument doc = JsonDocument.Parse(argsJson);
        JsonElement root = doc.RootElement;

        if (root.ValueKind != JsonValueKind.Array)
            return [];

        object?[] args = new object?[parameters.Length];
        for (int i = 0; i < Math.Min(root.GetArrayLength(), parameters.Length); i++)
        {
            JsonElement element = root[i];
            args[i] = element.Deserialize(parameters[i].ParameterType, SerializerOptions);
        }

        return args;
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
}

/// <summary>
/// Immutable snapshot of the background scheduler's most recent tick. Assigned by
/// reference (atomic in the CLR) so the single writer / many readers pattern is safe.
/// </summary>
public sealed record SchedulerTickStatus(DateTimeOffset? LastTickUtc, bool Succeeded, string? Error)
{
    /// <summary>Sentinel for "the scheduler has not completed a tick yet".</summary>
    public static readonly SchedulerTickStatus NotYetRun = new(null, Succeeded: false, Error: null);
}
