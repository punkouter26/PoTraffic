using PoTraffic.Api.Features.Maintenance;
using PoTraffic.Api.Infrastructure.Scheduling;
using PoTraffic.Api.Infrastructure.Storage;
using Serilog.Context;

namespace PoTraffic.Api.Infrastructure;

internal static class StartupTasksExtensions
{
    private const int MaxHydrationAttempts = 5;

    /// <summary>
    /// Kicks off cold-start work without blocking the host: hydrates the working set from
    /// Table Storage (with retry) and registers the nightly pruning job. Idempotent.
    /// <para>
    /// Hydration runs OFF the startup path so a transient Storage blip (RBAC propagation gap,
    /// 403 during cold start) never causes a 500.30 — the host binds immediately, <c>/health</c>
    /// returns 200, and <c>/health/ready</c> returns 503 until the working set is loaded. A
    /// permanent Production failure still stops the host loudly, but only after traffic can be
    /// served. In Dev/Test the context degrades to memory-only instead.
    /// </para>
    /// </summary>
    internal static WebApplication RunStartupTasks(this WebApplication app)
    {
        ILogger<Program> log = app.Services.GetRequiredService<ILogger<Program>>();

        _ = Task.Run(() => HydrateAsync(app, log));
        ScheduleRecurringJobs(app, log);

        return app;
    }

    private static async Task HydrateAsync(WebApplication app, ILogger<Program> log)
    {
        TableStorageContext db = app.Services.GetRequiredService<TableStorageContext>();

        // Tag every hydration log event with the storage account + credential source so
        // App Insights can filter by either (e.g. Storage.Account, Storage.Auth).
        string accountName = app.Configuration["AzureTable:AccountName"] ?? "<none>";
        string credentialSource = !string.IsNullOrWhiteSpace(app.Configuration["AZURE_CLIENT_ID"])
            ? "user-assigned"
            : "system-assigned";

        using (LogContext.PushProperty("Storage.Account", accountName))
        using (LogContext.PushProperty("Storage.Auth", credentialSource))
        using (LogContext.PushProperty("Operation", "HydrateAsync"))
        {
            TimeSpan backoff = TimeSpan.FromSeconds(5);
            for (int attempt = 1; attempt <= MaxHydrationAttempts; attempt++)
            {
                try
                {
                    await db.HydrateAsync();
                    log.LogInformation(
                        "Table Storage hydrated: {Users} users, {Routes} routes, {Polls} polls (attempt {Attempt}).",
                        db.Users.Count(), db.Routes.Count(), db.Polls.Count(), attempt);

                    // Seed default SystemConfiguration rows (cost rates, daily quota) and persist.
                    db.SeedDefaultConfigurationsIfMissing();
                    await db.SaveChangesAsync();
                    return;
                }
                catch (Exception ex) when (!app.Environment.IsProduction())
                {
                    // Dev/Test fallback: degrade to memory-only.
                    db.MarkVolatile();
                    log.LogWarning(ex,
                        "Table Storage unreachable (attempt {Attempt}/{Max}) — running MEMORY-ONLY. " +
                        "Start Azurite (docker compose up -d) to persist.", attempt, MaxHydrationAttempts);
                    return;
                }
                catch (Exception ex) when (attempt < MaxHydrationAttempts)
                {
                    log.LogError(ex,
                        "Table Storage hydration failed (attempt {Attempt}/{Max}); retrying in {BackoffSec}s.",
                        attempt, MaxHydrationAttempts, backoff.TotalSeconds);
                    try
                    {
                        await Task.Delay(backoff, app.Lifetime.ApplicationStopping);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, 60));
                }
                catch (Exception ex)
                {
                    // Final Production failure: stop the host with a loud signal.
                    log.LogCritical(ex,
                        "Table Storage hydration failed after {Max} attempts — terminating host. " +
                        "Verify the App Service managed identity has 'Storage Table Data Contributor' " +
                        "on the storage account; RBAC propagation can take 3–5 minutes.", MaxHydrationAttempts);
                    app.Lifetime.StopApplication();
                    return;
                }
            }
        }
    }

    private static void ScheduleRecurringJobs(WebApplication app, ILogger<Program> log)
    {
        // Skipped in Testing where IJobScheduler is not registered.
        IJobScheduler? scheduler = app.Services.GetService<IJobScheduler>();
        try
        {
            scheduler?.ScheduleRecurring(
                "prune-old-poll-records",
                async () =>
                {
                    using AsyncServiceScope jobScope = app.Services.CreateAsyncScope();
                    PruneOldPollRecordsJob job = jobScope.ServiceProvider.GetRequiredService<PruneOldPollRecordsJob>();
                    await job.ExecuteAsync();
                },
                "0 2 * * *"); // 02:00 UTC nightly
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Recurring job registration failed — nightly poll-record pruning will not run.");
        }
    }
}
