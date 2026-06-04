using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace PoTraffic.Api.Infrastructure.Scheduling;

/// <summary>
/// BackgroundService that ticks every second, queries Azurite for due jobs,
/// and executes them within a DI scope. Replaces Hangfire's background server.
/// </summary>
public sealed class BackgroundSchedulerService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly TableStorageJobScheduler? _tableScheduler;
    private readonly ILogger<BackgroundSchedulerService> _logger;
    private static readonly ActivitySource s_activitySource = new("PoTraffic.Scheduler");

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

        using PeriodicTimer timer = new(TimeSpan.FromSeconds(1));

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await ProcessJobs(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "BackgroundSchedulerService tick failed — will retry next tick");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("BackgroundSchedulerService stopped");
        }
    }

    private async Task ProcessJobs(CancellationToken ct)
    {
        if (_tableScheduler is null)
            return;

        // Process one-shot jobs
        IReadOnlyList<ScheduledJobEntity> oneShotJobs = _tableScheduler.GetDueOneShotJobs();
        foreach (ScheduledJobEntity job in oneShotJobs)
        {
            if (ct.IsCancellationRequested) break;
            await ExecuteOneShotJob(job, ct);
        }

        // Process recurring jobs
        IReadOnlyList<ScheduledJobEntity> recurringJobs = _tableScheduler.GetDueRecurringJobs();
        foreach (ScheduledJobEntity job in recurringJobs)
        {
            if (ct.IsCancellationRequested) break;
            await ExecuteRecurringJob(job, ct);
        }
    }

    private async Task ExecuteOneShotJob(ScheduledJobEntity job, CancellationToken ct)
    {
        using Activity? activity = s_activitySource.StartActivity(
            $"Job.{job.TypeName.Split('.').Last()}.{job.MethodName}");

        activity?.SetTag("job.id", job.RowKey);
        activity?.SetTag("job.type", job.TypeName);
        activity?.SetTag("job.method", job.MethodName);

        _tableScheduler!.MarkRunning(job);

        try
        {
            await InvokeJobMethod(job, ct);
            _tableScheduler.MarkCompleted(job);
            _logger.LogDebug("One-shot job {JobId} completed", job.RowKey);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "One-shot job {JobId} failed", job.RowKey);
            _tableScheduler.MarkFailed(job, ex.Message);
        }
    }

    private async Task ExecuteRecurringJob(ScheduledJobEntity job, CancellationToken ct)
    {
        using Activity? activity = s_activitySource.StartActivity(
            $"Job.{job.TypeName.Split('.').Last()}.{job.MethodName}");

        activity?.SetTag("job.id", job.RowKey);
        activity?.SetTag("job.type", job.TypeName);
        activity?.SetTag("job.method", job.MethodName);
        activity?.SetTag("job.cron", job.CronExpression);

        _tableScheduler!.MarkRunning(job);

        try
        {
            await InvokeJobMethod(job, ct);
            _tableScheduler.MarkRecurringCompleted(job);
            _logger.LogDebug("Recurring job {JobId} completed, next fire at {FireAt}",
                job.RowKey, job.FireAt);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Recurring job {JobId} failed", job.RowKey);
            _tableScheduler.MarkFailed(job, ex.Message);
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

        // Create a DI scope for the job (matches Hangfire's HangfireJobActivator behavior)
        await using AsyncServiceScope scope = _serviceProvider.CreateAsyncScope();
        object jobInstance = scope.ServiceProvider.GetRequiredService(jobType);

        object?[] args = DeserializeArgs(job.ArgsJson, method.GetParameters());

        Task result;
        if (method.ReturnType == typeof(Task))
        {
            result = (Task)method.Invoke(jobInstance, args)!;
        }
        else if (method.ReturnType == typeof(Task<>))
        {
            // For Task<T>, we still await it but discard the result
            result = (Task)method.Invoke(jobInstance, args)!;
        }
        else
        {
            // Sync method — wrap in Task.CompletedTask
            method.Invoke(jobInstance, args);
            return;
        }

        await result.WaitAsync(ct);
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
