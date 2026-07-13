using Azure.Extensions.AspNetCore.Configuration.Secrets;
using Azure.Data.Tables;
using Azure.Identity;
using FluentValidation;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using PoTraffic.Api.Features.Account;
using PoTraffic.Api.Features.Admin;
using PoTraffic.Api.Features.Alerts;
using PoTraffic.Api.Features.Auth;
using PoTraffic.Api.Features.Config;
using PoTraffic.Api.Features.Diagnostics;
using PoTraffic.Api.Features.History;
using PoTraffic.Api.Features.Maintenance;
using PoTraffic.Api.Features.MonitoringWindows;
using PoTraffic.Api.Features.Places;
using PoTraffic.Api.Features.Routes;
using PoTraffic.Api.Infrastructure;
using PoTraffic.Api.Infrastructure.Caching;
using PoTraffic.Api.Infrastructure.Logging;
using PoTraffic.Api.Infrastructure.Observability;
using PoTraffic.Api.Infrastructure.Providers;
using PoTraffic.Api.Infrastructure.Resilience;
using PoTraffic.Api.Infrastructure.Scheduling;
using PoTraffic.Api.Infrastructure.Security;
using PoTraffic.Api.Infrastructure.Storage;
using PoTraffic.Api.Infrastructure.Testing;
using Scalar.AspNetCore;
using Serilog;

// ── Serilog bootstrap (MEL-only; all app code uses ILogger<T>) ───────────────
//
// Serilog.Debugging.SelfLog flushes any internal pipeline failures to stderr.
// In a 500.30 environment the MEL sink can stall before stdout flushes, so
// SelfLog is the last-resort signal that "Serilog itself is broken, not the
// host". Surface it as soon as the bootstrap logger is created.
Log.Logger = new LoggerConfiguration()
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .CreateBootstrapLogger();

Serilog.Debugging.SelfLog.Enable(msg =>
    Console.Error.WriteLine($"[serilog-selflog] {DateTime.UtcNow:O} {msg}"));

try
{
    WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

    // ── Static web assets in Testing environment ──────────────────────────────
    // Blazor WASM static web assets are only loaded automatically in Development
    // and from published output in Production. For the Testing environment (E2E)
    // we must explicitly opt-in so the Blazor client is served correctly.
    if (builder.Environment.IsEnvironment("Testing"))
        builder.WebHost.UseStaticWebAssets();

    // ── Azure Key Vault (must be added BEFORE service registrations) ─────────
    // Services read configuration eagerly at registration time (e.g. OAuth client secret).
    // Key Vault must be in the configuration pipeline first so that all services receive
    // the resolved secrets rather than the appsettings.json placeholder values.
    // PrefixKeyVaultSecretManager strips the "PoTraffic--" namespace prefix so that
    // e.g. "PoTraffic--ConnectionStrings--Default" → config key "ConnectionStrings:Default".
    //
    // Rule 10 (First-Run Success): In Development we try Key Vault first when configured,
    // but if `DefaultAzureCredential` cannot acquire a token (no `az login` available)
    // we fall back to appsettings.Development.json silently so a fresh checkout still boots.
    // In Production/Staging a Key Vault failure is treated as a hard error — secrets
    // MUST come from Key Vault, never from a checked-in appsettings file.
    string? vaultUri = builder.Configuration["KeyVault:Uri"];
    bool keyVaultConfigured = !string.IsNullOrWhiteSpace(vaultUri);
    bool isDev = builder.Environment.IsDevelopment();
    if (keyVaultConfigured)
    {
        // Refresh rotated secrets without a restart (the old wiring read secrets once at boot).
        var kvOptions = new AzureKeyVaultConfigurationOptions
        {
            Manager = new PrefixKeyVaultSecretManager(),
            ReloadInterval = TimeSpan.FromMinutes(30)
        };

        if (isDev)
        {
            // Chain of Responsibility — try the credential, swallow auth/credential failures
            // so first-time contributors without `az login` can still `dotnet run` locally.
            // In Dev we still try DefaultAzureCredential first because `az login` is the
            // typical path; if it fails we fall back to appsettings.Development.json.
            try
            {
                builder.Configuration.AddAzureKeyVault(
                    new Uri(vaultUri!),
                    new DefaultAzureCredential(),
                    kvOptions);
            }
            catch (Exception ex) when (
                ex is Azure.Identity.AuthenticationFailedException      // covers CredentialUnavailableException
                || ex is Azure.RequestFailedException
                || ex is AggregateException)
            {
                // DEV-ONLY: any Key Vault failure (no/expired `az login`, 403/disabled vault,
                // network unreachable, etc.) must NOT prevent a local boot. Rule 10
                // (First-Run Success) — fall back to appsettings.Development.json.
                Console.WriteLine(
                    $"[startup] Key Vault unreachable ({ex.GetType().Name}: {ex.Message.Split('\n')[0]}); " +
                    "falling back to appsettings.Development.json (DEV-ONLY). " +
                    "Run `az login` and ensure the vault subscription is enabled to load secrets from Key Vault.");
            }
        }
        else
        {
            // Production / Staging — pin the credential to a single managed identity so
            // a future "I forgot which MI the role is on" mistake becomes a deterministic
            // error. DefaultAzureCredential walks env → workload → chained MIs; that
            // ordering is fine for dev but hides the real identity in prod.
            //
            // AZURE_CLIENT_ID is set in App Service app settings (from Bicep) and points
            // at the shared user-assigned MI in PoShared. If unset (e.g. local prod
            // build), fall back to system-assigned — never chain.
            Azure.Core.TokenCredential kvCredential = !string.IsNullOrWhiteSpace(
                builder.Configuration["AZURE_CLIENT_ID"])
                ? new Azure.Identity.ManagedIdentityCredential(
                    Azure.Identity.ManagedIdentityId.FromUserAssignedClientId(builder.Configuration["AZURE_CLIENT_ID"]!))
                : new Azure.Identity.ManagedIdentityCredential(Azure.Identity.ManagedIdentityId.SystemAssigned);

            builder.Configuration.AddAzureKeyVault(
                new Uri(vaultUri!),
                kvCredential,
                kvOptions);
        }
    }

    // ── Infrastructure extension methods (grouped by responsibility) ──────────
    builder.AddObservability();
    builder.Services.AddTableStoragePersistence();
    builder.Services.AddTableStorageServices(builder.Configuration, builder.Environment);
    builder.Services.AddBackgroundJobScheduler(builder.Environment);
    // Background job types — BackgroundSchedulerService resolves these from DI
    // by the type name stored with each scheduled job.
    builder.Services.AddScoped<PollRouteJob>();
    builder.Services.AddScoped<PruneOldPollRecordsJob>();
    builder.Services.AddScoped<TripleTestShotJob>();
    builder.Services.AddSecurityServices(builder.Configuration, builder.Environment.EnvironmentName);
    builder.Services.AddTrafficProviders(builder.Configuration, builder.Environment);
    builder.Services.AddAlertServices();
    builder.Services.AddPlacesServices();

    // ── Request dispatch (validation-first, see Infrastructure/Dispatch) ─────
    builder.Services.AddDispatcher(typeof(Program).Assembly);

    // ── FluentValidation ──────────────────────────────────────────────────────
    builder.Services.AddValidatorsFromAssembly(typeof(Program).Assembly);

    // ── HTTP resilience pipelines are registered per-client via ───────────────
    //   Infrastructure/Resilience/ResiliencePipelineExtensions.AddResilienceHandler(name)
    // ── Hybrid cache (L1 in-proc, optional distributed L2) ────────────────────
    builder.Services.AddPoTrafficHybridCache();

    // ── Problem Details (RFC 7807) ────────────────────────────────────────────
    builder.Services.AddProblemDetails();
    // Chain of Responsibility pattern — GlobalExceptionHandler maps ValidationException → 422
    builder.Services.AddExceptionHandler<GlobalExceptionHandler>();

    // ── Health checks — cheap, no external-quota cost ────────────────────────
    // "keyvault" proves secrets resolved at runtime (the Microsoft OAuth client
    // secret comes from the PoTraffic--ExternalAuth--Microsoft--ClientSecret vault
    // secret). "trafficProvider" confirms the live GoogleMaps key is present.
    IConfiguration healthCfg = builder.Configuration;
    bool usingMockProviders = builder.Environment.IsEnvironment("Testing")
        || string.Equals(healthCfg["Features:UseMockProviders"], "true", StringComparison.OrdinalIgnoreCase);
    builder.Services.AddHealthChecks()
        .AddCheck("keyvault", () =>
        {
            if (usingMockProviders)
                return HealthCheckResult.Healthy("Key Vault not required (test/dev mock mode)");
            string? k = healthCfg["ExternalAuth:Microsoft:ClientSecret"];
            bool resolved = !string.IsNullOrWhiteSpace(k)
                && !k.StartsWith("REPLACE_WITH", StringComparison.OrdinalIgnoreCase);
            return resolved
                ? HealthCheckResult.Healthy("Key Vault secrets resolved")
                : HealthCheckResult.Unhealthy("Key Vault secret 'ExternalAuth:Microsoft:ClientSecret' not resolved");
        })
        .AddCheck("trafficProvider", () =>
        {
            if (usingMockProviders)
                return HealthCheckResult.Healthy("Mock provider (test/dev)");
            return !string.IsNullOrWhiteSpace(healthCfg["GoogleMaps:ApiKey"])
                ? HealthCheckResult.Healthy("Live GoogleMaps provider configured")
                : HealthCheckResult.Degraded("GoogleMaps:ApiKey missing — live provider will fail");
        })
        .AddCheck<StorageHealthCheck>("storage")
        .AddCheck<SchedulerHealthCheck>("scheduler");

    // ── OpenAPI (Scalar UI) ───────────────────────────────────────────────────
    builder.Services.AddOpenApi();

    // ── Reverse-proxy awareness (Azure App Service) ───────────────────────────
    // In production TLS terminates at the App Service front end; the app receives
    // plain HTTP with the original scheme in X-Forwarded-Proto. Honoring it lets
    // UseHttpsRedirection see the real scheme (no redirect loop) and stops the
    // "Failed to determine the https port for redirect" warning. The proxy IP is
    // not fixed, so clear the known-proxy allowlist (App Service isolates the net).
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
    });

    WebApplication app = builder.Build();

    // Must run before HTTPS redirection / auth so downstream middleware sees the
    // forwarded scheme, host, and client IP rather than the proxy's.
    app.UseForwardedHeaders();

    // ── Static assets for the hosted Blazor WASM client (must run BEFORE auth) ──
    // Classic hosted-WASM pipeline: UseBlazorFrameworkFiles serves /_framework/*
    // (the WASM runtime + assemblies) and UseStaticFiles serves /css, /lib,
    // /_content, and the scoped-CSS bundle from wwwroot. Both are middleware, so
    // they run ahead of the authorization pipeline and are exempt from the
    // deny-by-default FallbackPolicy (§4.5) that guards the API endpoints — the
    // WASM client must load before the user can sign in.
    //
    // Asset fingerprinting is disabled in PoTraffic.Client.csproj so the physical
    // file names (blazor.webassembly.js, PoTraffic.Client.bundle.scp.css) match the
    // references in index.html verbatim. That is what makes the classic middleware
    // pipeline sufficient — no MapStaticAssets() endpoint routing (and its
    // deny-by-default 401s) is needed.
    app.UseBlazorFrameworkFiles();
    app.UseStaticFiles();

    // ── Exception handling ────────────────────────────────────────────────────
    // GlobalExceptionHandler runs first (handles ValidationException → 422);
    // unhandled exceptions fall through to the default handler.
    app.UseExceptionHandler();

    if (app.Environment.IsDevelopment())
    {
        app.UseDeveloperExceptionPage();
        // OpenAPI document at /openapi/v1.json; Scalar UI at /scalar/v1.
        // Anonymous — dev-only API docs must not trip the deny-by-default fallback (§4.5).
        app.MapOpenApi().AllowAnonymous();
        app.MapScalarApiReference().AllowAnonymous();
    }

    app.UseStatusCodePages();

    // ── Security headers / HTTPS ─────────────────────────────────────────────
    app.UseHttpsRedirection();
    app.UseSerilogRequestLogging();

    // ── Auth middleware ───────────────────────────────────────────────────────
    app.UseAuthentication();
    app.UseAuthorization();

    // ── Log context enrichment — pushes UserId and Environment into every log event ──
    // Ensures Serilog + OTel log entries carry these properties in all sinks.
    app.UseMiddleware<LogContextEnrichmentMiddleware>();

    // ── API endpoints ─────────────────────────────────────────────────────────
    app.MapClientLogEndpoints();
    app.MapAccountEndpoints();
    app.MapAdminEndpoints();
    app.MapAuthEndpoints();
    app.MapDiagnosticsEndpoints();
    // GUEST login bypass: Development (Rule 4.4 split view) + Testing (automated tests).
    // Production registers Microsoft OAuth only; MapGuestEndpoints throws if it is
    // ever wired into a Production host.
    if (app.Environment.IsEnvironment("Testing") || app.Environment.IsDevelopment())
    {
        app.MapGuestEndpoints(app.Environment);
    }
    app.MapRoutesEndpoints();
    app.MapWindowsEndpoints();
    app.MapHistoryEndpoints();
    app.MapAlertsEndpoints();
    app.MapPlacesEndpoints();
    app.MapSystemEndpoints();
    app.MapDiagEndpoints();
    app.MapTestingEndpoints(app.Environment);

    // Error endpoint — anonymous so the exception handler re-execute path is never
    // blocked by the deny-by-default fallback (§4.5).
    app.MapGet("/error", () => Results.Problem()).ExcludeFromDescription().AllowAnonymous();

    // ── Readiness probe — distinct from /health so App Service readiness probes
    // don't depend on hydration completing. Returns 503 while HydrateAsync is in
    // flight; 200 once IsHydrated is true. The host binds first either way.
    app.MapGet("/health/ready", () =>
    {
        TableStorageContext ctx = app.Services.GetRequiredService<TableStorageContext>();
        return ctx.IsHydrated
            ? Results.Ok(new { status = "ready", durable = ctx.IsDurable })
            : Results.Json(new { status = "hydrating", durable = ctx.IsDurable }, statusCode: 503);
    }).AllowAnonymous();

    // ── Health check endpoint — pings DB and external APIs, returns JSON status per dependency ──
    app.MapHealthChecks("/health", new HealthCheckOptions
    {
        ResponseWriter = async (httpCtx, report) =>
        {
            httpCtx.Response.ContentType = "application/json";
            string result = System.Text.Json.JsonSerializer.Serialize(new
            {
                status = report.Status.ToString(),
                entries = report.Entries.Select(e => new
                {
                    name = e.Key,
                    status = e.Value.Status.ToString(),
                    description = e.Value.Description,
                    durationMs = e.Value.Duration.TotalMilliseconds
                })
            });
            await httpCtx.Response.WriteAsync(result);
        }
    }).AllowAnonymous();

    // Unknown API routes return a clean 404 instead of falling through to the SPA shell
    // (which would serve index.html for an API call, and — under the §4.5 deny-by-default
    // policy — turn a method mismatch into a misleading 401). Real endpoints are more
    // specific than this catch-all, so they always win; anonymous so the 404 isn't challenged.
    app.Map("/api/{**rest}", () => Results.NotFound()).AllowAnonymous().ExcludeFromDescription();

    // ── Client-side unhandled error sink ─────────────────────────────────────
    // Paired with the blazor-error-ui observer in wwwroot/js/blazor-error-reporter.js.
    // Anonymous so the bootstrap page can report load-time failures before sign-in.
    app.MapPost("/api/diag/client-error", (
        [FromBody] ClientErrorReport report,
        [FromServices] ILoggerFactory loggerFactory) =>
    {
        Microsoft.Extensions.Logging.ILogger logger = loggerFactory.CreateLogger("ClientErrors");
        // Forward to Serilog with structured fields so App Insights queries
        // can filter by route / UA / app version. No body echo to caller.
        logger.LogWarning(
            "ClientError url={Url} ua={UserAgent} version={AppVersion} message={Message}",
            report.Url, report.UserAgent, report.AppVersion, report.Message);
        return Results.NoContent();
    }).AllowAnonymous().ExcludeFromDescription();

    // ── SPA fallback (non-API GET requests) ──────────────────────────────────
    // Static assets are served above by UseBlazorFrameworkFiles/UseStaticFiles.
    // Anything else falls back to index.html so the client-side Blazor Router can
    // take over. Anonymous: the shell must load before sign-in, and client-side
    // <AuthorizeRouteView> gates the UI. The unknown-/api catch-all above already
    // 404s stray API routes, so this only ever serves real client-side routes.
    app.MapFallbackToFile("index.html").AllowAnonymous();

    // ── Startup: hydrate the working set from Table Storage + seed configuration ──
    // Idempotent — safe to run on every cold-start.
    //
    // Hydration runs OFF the host startup path so a transient Storage blip
    // (RBAC propagation gap, network glitch, 403 during cold start) never causes
    // a 500.30. The host binds immediately, /health returns 200, /health/ready
    // returns 503 until the working set is populated. A background retry loop
    // handles transient failures. In Production, a permanent failure still
    // crashes the host via `app.Lifetime.ApplicationStopping` so ops gets a
    // loud signal — but only AFTER traffic can already be served.
    ILogger<Program> startupLog = app.Services.GetRequiredService<ILogger<Program>>();
    TableStorageContext db = app.Services.GetRequiredService<TableStorageContext>();

    _ = Task.Run(async () =>
    {
        // Push Storage.AccountName + Storage.Auth onto every log event inside
        // the hydration scope so App Insights queries can filter by either
        // (e.g. "Storage.Account == 'potrafficstorage'" AND "Storage.Auth == 'user-assigned'").
        string accountName = builder.Configuration["AzureTable:AccountName"] ?? "<none>";
        string credentialSource = !string.IsNullOrWhiteSpace(builder.Configuration["AZURE_CLIENT_ID"])
            ? "user-assigned"
            : "system-assigned";

        using (Serilog.Context.LogContext.PushProperty("Storage.Account", accountName))
        using (Serilog.Context.LogContext.PushProperty("Storage.Auth", credentialSource))
        using (Serilog.Context.LogContext.PushProperty("Operation", "HydrateAsync"))
        {
            const int MaxAttempts = 5;
            TimeSpan backoff = TimeSpan.FromSeconds(5);
            for (int attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                try
                {
                    await db.HydrateAsync();
                    startupLog.LogInformation(
                        "Table Storage hydrated: {Users} users, {Routes} routes, {Polls} polls (attempt {Attempt}).",
                        db.Users.Count(), db.Routes.Count(), db.Polls.Count(), attempt);

                    // Seed default SystemConfiguration rows (cost rates, daily quota) and persist them.
                    db.SeedDefaultConfigurationsIfMissing();
                    await db.SaveChangesAsync();
                    return;
                }
                catch (Exception ex) when (!app.Environment.IsProduction())
                {
                    // Dev/Test fallback: degrade to memory-only.
                    db.MarkVolatile();
                    startupLog.LogWarning(ex,
                        "Table Storage unreachable (attempt {Attempt}/{Max}) — running MEMORY-ONLY. " +
                        "Start Azurite (docker compose up -d) to persist.", attempt, MaxAttempts);
                    return;
                }
                catch (Exception ex) when (app.Environment.IsProduction() && attempt < MaxAttempts)
                {
                    startupLog.LogError(ex,
                        "Table Storage hydration failed (attempt {Attempt}/{Max}); retrying in {BackoffSec}s.",
                        attempt, MaxAttempts, backoff.TotalSeconds);
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
                    // Final failure in Production: stop the host with a loud signal.
                    startupLog.LogCritical(ex,
                        "Table Storage hydration failed after {Max} attempts — terminating host. " +
                        "Verify the App Service managed identity has 'Storage Table Data Contributor' " +
                        "on the storage account; RBAC propagation can take 3–5 minutes.", MaxAttempts);
                    app.Lifetime.StopApplication();
                    return;
                }
            }
        }
    });

    // T086 — Register nightly pruning recurring job (02:00 UTC).
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
            "0 2 * * *");
    }
    catch (Exception ex)
    {
        startupLog.LogError(ex, "Recurring job registration failed — nightly poll-record pruning will not run.");
    }

    app.Run();
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, "Host terminated unexpectedly.");
}
finally
{
    Log.CloseAndFlush();
}

// Marker so WebApplicationFactory<Program> can reference the host in integration tests.
public partial class Program { }

/// <summary>
/// Payload shape for the client-side unhandled error reports posted to
/// <c>/api/diag/client-error</c>. Keep fields primitive so the System.Text.Json
/// source-generated payloads succeed without reflection under AOT.
/// </summary>
public sealed record ClientErrorReport(
    [property: System.Text.Json.Serialization.JsonPropertyName("url")] string Url,
    [property: System.Text.Json.Serialization.JsonPropertyName("userAgent")] string? UserAgent,
    [property: System.Text.Json.Serialization.JsonPropertyName("appVersion")] string? AppVersion,
    [property: System.Text.Json.Serialization.JsonPropertyName("message")] string? Message,
    [property: System.Text.Json.Serialization.JsonPropertyName("stack")] string? Stack);
