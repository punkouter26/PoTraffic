using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using Azure;
using Azure.Data.Tables;

namespace PoTraffic.Api.Infrastructure.Scheduling;

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

    public string Enqueue(Expression<Func<Task>> job)
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
            FireAt = DateTimeOffset.UtcNow,
            Status = "Pending"
        };

        _tableClient.UpsertEntity(entity);
        return jobId;
    }

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

    public void Cancel(string jobId)
    {
        try
        {
            Response<ScheduledJobEntity> response = _tableClient.GetEntity<ScheduledJobEntity>(
                OneShotPartition, jobId);
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

    public int CancelPendingPollJobsForRoute(Guid routeId)
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

    public void ScheduleRecurring(string jobId, Func<Task> job, string cronExpression)
    {
        DateTimeOffset nextFireAt = ComputeNextFire(cronExpression, DateTimeOffset.UtcNow);

        // Store the function reference so BackgroundSchedulerService can invoke it
        RecurringJobFunctions[jobId] = job;

        var entity = new ScheduledJobEntity
        {
            PartitionKey = RecurringPartition,
            RowKey = jobId,
            TypeName = nameof(Func<Task>),
            MethodName = "Invoke",
            ArgsJson = "[]",
            FireAt = nextFireAt,
            Status = "Pending",
            CronExpression = cronExpression
        };

        _tableClient.UpsertEntity(entity);
    }

    public void CancelRecurring(string jobId)
    {
        try
        {
            Response<ScheduledJobEntity> response = _tableClient.GetEntity<ScheduledJobEntity>(
                RecurringPartition, jobId);
            ScheduledJobEntity entity = response.Value;
            entity.Status = "Cancelled";
            _tableClient.UpdateEntity(entity, ETag.All);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            // Already cancelled or doesn't exist
        }
    }

    /// <summary>
    /// Query pending one-shot jobs that are due to fire.
    /// Used by BackgroundSchedulerService.
    /// </summary>
    public IReadOnlyList<ScheduledJobEntity> GetDueOneShotJobs()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return _tableClient
            .Query<ScheduledJobEntity>(e =>
                e.PartitionKey == OneShotPartition &&
                e.Status == "Pending" &&
                e.FireAt <= now)
            .ToList();
    }

    /// <summary>
    /// Query pending recurring jobs that are due to fire.
    /// Used by BackgroundSchedulerService.
    /// </summary>
    public IReadOnlyList<ScheduledJobEntity> GetDueRecurringJobs()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return _tableClient
            .Query<ScheduledJobEntity>(e =>
                e.PartitionKey == RecurringPartition &&
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
        entity.FireAt = ComputeNextFire(entity.CronExpression!, DateTimeOffset.UtcNow);
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
            entity.FireAt = ComputeNextFire(entity.CronExpression!, DateTimeOffset.UtcNow);
        }
        UpdateAndRefreshETag(entity);
    }

    /// <summary>
    /// Route ids that currently have a live (Pending or Running) one-shot poll job.
    /// Used by startup reconciliation to tell a sleeping/active chain (has a live job)
    /// from a dead one (crashed without scheduling a successor).
    /// </summary>
    public HashSet<Guid> GetRouteIdsWithLivePollJobs()
    {
        var live = new HashSet<Guid>();
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
                    && Guid.TryParse(doc.RootElement[0].GetString(), out Guid routeId))
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
            if (job.PartitionKey == RecurringPartition && !string.IsNullOrEmpty(job.CronExpression))
                job.FireAt = ComputeNextFire(job.CronExpression, DateTimeOffset.UtcNow);
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

    internal static DateTimeOffset ComputeNextFire(string cronExpression, DateTimeOffset after)
    {
        // Simple CRON parser for standard 5-field expressions: minute hour dayOfMonth month dayOfWeek
        // Delegates to ParseCronFields for the actual calculation.
        string[] parts = cronExpression.Split(' ');
        if (parts.Length != 5)
            throw new ArgumentException($"Invalid CRON expression: {cronExpression}");

        return ParseCronNextFire(parts, after);
    }

    private static DateTimeOffset ParseCronNextFire(string[] fields, DateTimeOffset after)
    {
        DateTimeOffset candidate = after.AddMinutes(1).ToUniversalTime();
        candidate = new DateTimeOffset(candidate.Year, candidate.Month, candidate.Day,
            candidate.Hour, candidate.Minute, 0, TimeSpan.Zero);

        // Limit search to 366 days to avoid infinite loops
        DateTimeOffset limit = after.AddDays(366);

        while (candidate < limit)
        {
            if (MatchesCronField(fields[0], candidate.Minute) &&
                MatchesCronField(fields[1], candidate.Hour) &&
                MatchesCronField(fields[2], candidate.Day) &&
                MatchesCronField(fields[3], candidate.Month) &&
                MatchesCronField(fields[4], (int)candidate.DayOfWeek))
            {
                return candidate;
            }

            candidate = candidate.AddMinutes(1);
        }

        // Fallback: return 1 hour from now
        return after.AddHours(1);
    }

    private static bool MatchesCronField(string field, int value)
    {
        if (field == "*") return true;

        // Handle comma-separated values (e.g. "1,3,5")
        if (field.Contains(','))
            return field.Split(',').Any(f => MatchesCronField(f.Trim(), value));

        // Handle ranges (e.g. "1-5")
        if (field.Contains('-'))
        {
            string[] range = field.Split('-');
            int min = int.Parse(range[0]);
            int max = int.Parse(range[1]);
            return value >= min && value <= max;
        }

        // Handle step values (e.g. "*/5")
        if (field.Contains('/'))
        {
            string[] step = field.Split('/');
            int interval = int.Parse(step[1]);
            if (step[0] == "*")
                return value % interval == 0;
            int start = int.Parse(step[0]);
            return value >= start && (value - start) % interval == 0;
        }

        return int.Parse(field) == value;
    }

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
