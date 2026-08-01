using System.Collections.Concurrent;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using Azure;
using Azure.Data.Tables;

namespace PoTraffic.API.Infrastructure.Scheduling;

/// <summary>
/// IJobScheduler implementation backed by Azure Table Storage (Azurite locally).
/// Persists job state to durable tables so scheduled jobs survive process restarts.
/// </summary>
public sealed class TableStorageJobScheduler : IJobScheduler
{
    private readonly TableClient _tableClient;
    internal const string TableName = "ScheduledJobs";
    private const string OneShotPartition = "OneShot";
    private const string RecurringPartition = "Recurring";

    /// <summary>
    /// Stores Func<Task> references for recurring jobs so they can be invoked
    /// without expression tree serialization (async lambdas can't be in expression trees).
    /// </summary>
    internal static readonly ConcurrentDictionary<string, Func<Task>> RecurringJobFunctions = new();

    public TableStorageJobScheduler(TableServiceClient tableServiceClient)
    {
        try
        {
            tableServiceClient.CreateTableIfNotExists(TableName);
        }
        catch (RequestFailedException ex) when (ex.Status == 409)
        {
            // 409 Conflict — table already exists. Benign.
        }
        catch (RequestFailedException ex)
        {
            // Any other failure (e.g. a malformed endpoint returning 400) must NOT be
            // swallowed silently — it previously masked the root cause of every-tick
            // failures. Surface it so the scheduler's broken state is diagnosable.
            Console.WriteLine(
                $"[startup] TableStorageJobScheduler could not ensure table '{TableName}' " +
                $"({ex.Status} {ex.ErrorCode}): {ex.Message.Split('\n')[0]}");
        }
        _tableClient = tableServiceClient.GetTableClient(TableName);
    }

    public string Enqueue(Expression<Func<Task>> job) => Schedule(job, TimeSpan.Zero);

    public string Schedule(Expression<Func<Task>> job, TimeSpan delay)
    {
        JobInvocationInfo info = ExtractInvocationInfo(job);
        string jobId = Guid.NewGuid().ToString("N");

        var entity = new ScheduledJobEntity
        {
            PartitionKey = OneShotPartition,
            RowKey = jobId,
            TypeName = info.TypeName,
            MethodName = info.MethodName,
            ArgsJson = info.ArgsJson,
            FireAt = DateTimeOffset.UtcNow.Add(delay),
            Status = "Pending"
        };

        _tableClient.UpsertEntity(entity);
        return jobId;
    }

    public void Cancel(string jobId) => CancelIn(OneShotPartition, jobId);

    private void CancelIn(string partition, string jobId)
    {
        try
        {
            Response<ScheduledJobEntity> response = _tableClient.GetEntity<ScheduledJobEntity>(
                partition, jobId);
            ScheduledJobEntity entity = response.Value;
            entity.Status = "Cancelled";
            // Unconditional (ETag.All): a concurrent MarkRunning bumps the ETag, which would
            // 412 an IfMatch update and bubble a 500 out of Delete/Stop/Update route commands.
            // Cancellation is a best-effort "make it Cancelled", so last-writer-wins is correct.
            _tableClient.UpdateEntity(entity, ETag.All);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            // Job already completed or doesn't exist — no-op
        }
    }

    public int CancelPendingPollJobsForRoute(RouteId routeId)
    {
        // PollRouteJob.Execute(routeId) serializes its arg to the ArgsJson array, so the
        // route id appears verbatim in the JSON. Match pending one-shot Execute jobs for
        // this route and cancel them; the currently-executing job is "Running", not
        // "Pending", so it is never caught here.
        string needle = routeId.ToString();
        List<ScheduledJobEntity> duplicates = _tableClient
            .Query<ScheduledJobEntity>(e =>
                e.PartitionKey == OneShotPartition && e.Status == "Pending")
            .Where(e => e.MethodName == "Execute"
                && e.ArgsJson != null
                && e.ArgsJson.Contains(needle, StringComparison.OrdinalIgnoreCase))
            .ToList();

        int cancelled = 0;
        foreach (ScheduledJobEntity job in duplicates)
        {
            job.Status = "Cancelled";
            try
            {
                _tableClient.UpdateEntity(job, job.ETag);
                cancelled++;
            }
            catch (RequestFailedException)
            {
                // Lost the race (job started or was already changed) — safe to ignore.
            }
        }
        return cancelled;
    }

    public void ScheduleRecurring(string jobId, Func<Task> job, TimeOnly dailyAtUtc)
    {
        // Store the function reference so BackgroundSchedulerService can invoke it
        RecurringJobFunctions[jobId] = job;

        var entity = new ScheduledJobEntity
        {
            PartitionKey = RecurringPartition,
            RowKey = jobId,
            TypeName = nameof(Func<Task>),
            MethodName = "Invoke",
            ArgsJson = "[]",
            FireAt = ComputeNextFire(dailyAtUtc, DateTimeOffset.UtcNow),
            Status = "Pending",
            DailyAtUtc = dailyAtUtc.ToString("HH:mm", CultureInfo.InvariantCulture)
        };

        _tableClient.UpsertEntity(entity);
    }

    /// <summary>Pending one-shot jobs that are due to fire. Used by BackgroundSchedulerService.</summary>
    public IReadOnlyList<ScheduledJobEntity> GetDueOneShotJobs() => GetDueIn(OneShotPartition);

    /// <summary>Pending recurring jobs that are due to fire. Used by BackgroundSchedulerService.</summary>
    public IReadOnlyList<ScheduledJobEntity> GetDueRecurringJobs() => GetDueIn(RecurringPartition);

    private IReadOnlyList<ScheduledJobEntity> GetDueIn(string partition)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return _tableClient
            .Query<ScheduledJobEntity>(e =>
                e.PartitionKey == partition &&
                e.Status == "Pending" &&
                e.FireAt <= now)
            .ToList();
    }

    /// <summary>
    /// Mark a job as running.
    /// </summary>
    public void MarkRunning(ScheduledJobEntity entity)
    {
        entity.Status = "Running";
        entity.AttemptCount++;
        UpdateAndRefreshETag(entity);
    }

    /// <summary>
    /// Mark a one-shot job as completed.
    /// </summary>
    public void MarkCompleted(ScheduledJobEntity entity)
    {
        entity.Status = "Completed";
        UpdateAndRefreshETag(entity);
    }

    /// <summary>
    /// Mark a recurring job as completed and schedule its next execution.
    /// </summary>
    public void MarkRecurringCompleted(ScheduledJobEntity entity)
    {
        entity.Status = "Pending";
        entity.LastRunAt = DateTimeOffset.UtcNow;
        entity.FireAt = NextFireFor(entity, DateTimeOffset.UtcNow);
        entity.AttemptCount = 0;
        UpdateAndRefreshETag(entity);
    }

    /// <summary>
    /// Mark a job as failed with an error message.
    /// </summary>
    public void MarkFailed(ScheduledJobEntity entity, string error)
    {
        entity.Status = entity.PartitionKey == RecurringPartition ? "Pending" : "Completed";
        entity.ErrorMessage = error;
        if (entity.PartitionKey == RecurringPartition)
        {
            entity.FireAt = NextFireFor(entity, DateTimeOffset.UtcNow);
        }
        UpdateAndRefreshETag(entity);
    }

    /// <summary>
    /// Route ids that currently have a live (Pending or Running) one-shot poll job.
    /// Used by startup reconciliation to tell a sleeping/active chain (has a live job)
    /// from a dead one (crashed without scheduling a successor).
    /// </summary>
    public HashSet<RouteId> GetRouteIdsWithLivePollJobs()
    {
        var live = new HashSet<RouteId>();
        foreach (ScheduledJobEntity e in _tableClient.Query<ScheduledJobEntity>(e =>
            e.PartitionKey == OneShotPartition && (e.Status == "Pending" || e.Status == "Running")))
        {
            if (e.MethodName != "Execute" || string.IsNullOrEmpty(e.ArgsJson))
                continue;
            try
            {
                using JsonDocument doc = JsonDocument.Parse(e.ArgsJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Array
                    && doc.RootElement.GetArrayLength() > 0
                    && doc.RootElement[0].ValueKind == JsonValueKind.String
                    && RouteId.TryParse(doc.RootElement[0].GetString(), null, out RouteId routeId))
                {
                    live.Add(routeId);
                }
            }
            catch (JsonException)
            {
                // Unparseable args — ignore for reconciliation purposes.
            }
        }
        return live;
    }

    /// <summary>
    /// Requeues one-shot jobs left in "Running" by a crash or a failed status
    /// update. Called once at scheduler startup — nothing can legitimately be
    /// running before the worker loop starts.
    /// </summary>
    public int RequeueStaleRunningJobs()
    {
        // Covers BOTH partitions: a recurring job (e.g. nightly pruning) stuck in "Running"
        // by a crash is skipped by the due-query (which only returns "Pending"), so without
        // this it would never run again. Recompute its FireAt so it fires promptly.
        List<ScheduledJobEntity> stale = _tableClient
            .Query<ScheduledJobEntity>(e => e.Status == "Running")
            .ToList();

        foreach (ScheduledJobEntity job in stale)
        {
            job.Status = "Pending";
            if (job.PartitionKey == RecurringPartition)
                job.FireAt = NextFireFor(job, DateTimeOffset.UtcNow);
            _tableClient.UpdateEntity(job, ETag.All);
        }

        return stale.Count;
    }

    /// <summary>
    /// The SDK does not refresh <c>entity.ETag</c> after an update, so a second
    /// status transition on the same instance (MarkRunning → MarkCompleted/Failed)
    /// would 412. Capture the new ETag from the response.
    /// </summary>
    private void UpdateAndRefreshETag(ScheduledJobEntity entity)
    {
        Response response = _tableClient.UpdateEntity(entity, entity.ETag);
        if (response.Headers.ETag is { } etag)
            entity.ETag = etag;
    }

    /// <summary>
    /// Next occurrence of <paramref name="timeOfDayUtc"/> strictly after <paramref name="after"/>.
    /// </summary>
    internal static DateTimeOffset ComputeNextFire(TimeOnly timeOfDayUtc, DateTimeOffset after)
    {
        DateTimeOffset todayAtTime = new(
            after.UtcDateTime.Date.Add(timeOfDayUtc.ToTimeSpan()), TimeSpan.Zero);

        return todayAtTime > after ? todayAtTime : todayAtTime.AddDays(1);
    }

    /// <summary>
    /// Next fire time for a stored recurring job. A row whose <see cref="ScheduledJobEntity.DailyAtUtc"/>
    /// is missing or unparseable is pushed a day out rather than left permanently due — that would
    /// spin the job every tick.
    /// </summary>
    private static DateTimeOffset NextFireFor(ScheduledJobEntity entity, DateTimeOffset after)
        => TimeOnly.TryParse(entity.DailyAtUtc, CultureInfo.InvariantCulture, out TimeOnly at)
            ? ComputeNextFire(at, after)
            : after.AddDays(1);

    private static JobInvocationInfo ExtractInvocationInfo(Expression<Func<Task>> job)
    {
        LambdaExpression lambda = job;
        if (lambda.Body is MethodCallExpression methodCall)
        {
            string typeName = methodCall.Method.DeclaringType?.FullName
                ?? throw new InvalidOperationException("Cannot determine job type from expression.");

            string methodName = methodCall.Method.Name;

            object?[] args = methodCall.Arguments
                .Select(EvaluateExpression)
                .ToArray();

            string argsJson = JsonSerializer.Serialize(args, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });

            return new JobInvocationInfo(typeName, methodName, argsJson);
        }

        throw new ArgumentException(
            $"Expression must be a method call (e.g. j => j.Execute(id)). Got: {lambda.Body.NodeType}");
    }

    private static object? EvaluateExpression(Expression expression)
    {
        // Handle constant expressions (literal values)
        if (expression is ConstantExpression constant)
            return constant.Value;

        // Handle captured lambda variables (e.g. routeId from closure)
        if (expression is MemberExpression member)
        {
            // Walk up the member chain to find the root constant
            object? container = null;
            if (member.Expression is ConstantExpression rootConst)
                container = rootConst.Value;

            if (container is not null)
            {
                FieldInfo? field = container.GetType().GetField(member.Member.Name,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (field is not null)
                    return field.GetValue(container);
            }
        }

        // Fallback: compile and evaluate
        Expression<Func<object>> lambda =
            Expression.Lambda<Func<object>>(
                Expression.Convert(expression, typeof(object)));
        return lambda.Compile().Invoke();
    }

    private sealed record JobInvocationInfo(string TypeName, string MethodName, string ArgsJson);
}
