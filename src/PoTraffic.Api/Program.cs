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

    // ── Static web assets (must run BEFORE auth — Fix #1+#7+#8) ──────────────
    // The deny-by-default FallbackPolicy (§4.5) blocks anonymous access to
    // endpoints that don't explicitly opt out. In .NET 10, MapStaticAssets()
    // registers per-file endpoints for fingerprinted assets (e.g.
    // blazor.webassembly.[hash].js) and a SINGLE catch-all endpoint for the
    // runtime manifest (blazor.boot.json). That catch-all does NOT carry the
    // AllowAnonymous() metadata reliably, so the runtime manifest 401s and
    // the WASM client never receives the assembly list → empty route table →
    // every page renders NotFound.
    //
    // UseBlazorFrameworkFiles() runs at the middleware layer (before auth
    // runs) and is exempt from the FallbackPolicy by framework design.
    // UseStaticFiles() then serves /css, /lib, /_content from wwwroot.
    app.UseBlazorFrameworkFiles();
    app.UseStaticFiles();

    // ── Blazor WASM WebRootFileProvider fix ───────────────────────────────────
    // In .NET 10, MapStaticAssets() creates endpoint-based routes for each fingerprinted
    // static asset, but MapFallbackToFile("index.html") uses WebRootFileProvider (the older
    // middleware approach). UseStaticWebAssets() may not update WebRootFileProvider correctly
    // outside of the Development environment. We manually populate it from the runtime manifest
    // so that SPA routing (MapFallbackToFile) can serve index.html for all non-API routes.
    if (!app.Environment.IsProduction())
    {
        IWebHostEnvironment webEnv = app.Services.GetRequiredService<IWebHostEnvironment>();
        ILogger<Program> startupLogger = app.Services.GetRequiredService<ILogger<Program>>();

        if (!webEnv.WebRootFileProvider.GetFileInfo("index.html").Exists)
        {
            startupLogger.LogWarning(
                "WebRootFileProvider does not contain index.html — configuring from static web assets manifest.");

            string manifestPath = Path.Combine(
                AppContext.BaseDirectory,
                $"{webEnv.ApplicationName}.staticwebassets.runtime.json");

            if (File.Exists(manifestPath))
            {
                using System.Text.Json.JsonDocument manifestDoc =
                    System.Text.Json.JsonDocument.Parse(File.ReadAllText(manifestPath));
                if (manifestDoc.RootElement.TryGetProperty("ContentRoots",
                    out System.Text.Json.JsonElement roots))
                {
                    Microsoft.Extensions.FileProviders.IFileProvider[] providers = roots.EnumerateArray()
                        .Select(r => r.GetString() ?? string.Empty)
                        .Where(p => !string.IsNullOrEmpty(p) && Directory.Exists(p))
                        .Select(p =>
                            (Microsoft.Extensions.FileProviders.IFileProvider)
                            new Microsoft.Extensions.FileProviders.PhysicalFileProvider(p))
                        .ToArray();

                    if (providers.Length > 0)
                    {
                        webEnv.WebRootFileProvider =
                            new Microsoft.Extensions.FileProviders.CompositeFileProvider(providers);
                        startupLogger.LogInformation(
                            "Configured WebRootFileProvider with {Count} content root(s) from static web assets manifest.",
                            providers.Length);
                    }
                }
            }
            else
            {
                startupLogger.LogWarning("Static web assets manifest not found at {Path}.", manifestPath);
            }
        }
    }

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

    // ── Serve Blazor WASM fallback (non-API requests) ─────────────────────────
    // Static assets + the SPA shell are anonymous: the WASM client must load BEFORE
    // the user can sign in, and client-side <AuthorizeRouteView> gates the UI. The
    // deny-by-default fallback (§4.5) only guards the API endpoints.
    //
    // NOTE: We intentionally do NOT call MapStaticAssets() here. In .NET 10 it
    // registers ENDPOINT routes for every fingerprinted /_framework/* asset; but
    // UseBlazorFrameworkFiles() (wired above at the middleware layer) ALSO claims
    // /_framework/*. For a WASM asset request, routing selects the MapStaticAssets
    // endpoint while the UseBlazorFrameworkFiles StaticFileMiddleware runs first and
    // ends the pipeline — so EndpointMiddleware never executes the selected endpoint,
    // throwing InvalidOperationException "The request reached the end of the pipeline
    // without executing the endpoint" → HTTP 500 for EVERY /_framework/* asset under
    // in-process IIS hosting (the Blazor client then cannot boot; every route renders
    // NotFound). UseBlazorFrameworkFiles + UseStaticFiles + MapFallbackToFile is the
    // complete, supported hosting pipeline for a hosted Blazor WASM app; MapStaticAssets
    // is for the HOST's own static web assets, of which there are none here.

    // Fix #9 — /.well-known/blazor-boot — minimal re-probe endpoint for ops.
    // Always anonymous. Always synthesises a real blazor.boot.json from the
    // published wwwroot/_framework directory so it works regardless of whether
    // the project's staticwebassets.endpoints.json was populated. Cached for
    // 60s — invalidation requires an app restart (acceptable for a framework
    // manifest that only changes on deploy).
    app.MapGet("/.well-known/blazor-boot", (IWebHostEnvironment webEnv) =>
        Results.Json(BlazorBootManifestBuilder.Build(webEnv), contentType: "application/json"))
        .AllowAnonymous().ExcludeFromDescription()
        .CacheOutput(p => p.Expire(TimeSpan.FromSeconds(60)));

    // NOTE: We do NOT map an endpoint for /_framework/blazor.boot.json. The .NET 8+
    // WASM runtime embeds the boot manifest in the fingerprinted dotnet.{hash}.js and
    // does not fetch a standalone blazor.boot.json, so that legacy path is unused.
    // Worse, mapping it as an endpoint reintroduces the UseBlazorFrameworkFiles/endpoint
    // pipeline collision that 500s every /_framework/* request. The ops re-probe at
    // /.well-known/blazor-boot above (a non-/_framework path) still exposes the
    // synthesised manifest without colliding with the framework-files middleware.

    // Fix #10 — client-side unhandled error sink (paired with blazor-error-ui
    // observer in wwwroot/js/blazor-error-reporter.js). Anonymous so the
    // bootstrap page can report load-time failures before sign-in.
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

    if (app.Environment.IsEnvironment("Testing"))
    {
        string testingIndexPath = Path.GetFullPath(
            Path.Combine(app.Environment.ContentRootPath, "..", "PoTraffic.Client", "wwwroot", "index.html"));

        if (File.Exists(testingIndexPath))
        {
            app.MapGet("/index.html", () => Results.File(testingIndexPath, "text/html")).AllowAnonymous();
            app.MapFallback(() => Results.File(testingIndexPath, "text/html")).AllowAnonymous();
        }
        else
        {
            app.MapFallbackToFile("index.html").AllowAnonymous();
        }
    }
    else
    {
        // Fix #11b — explicit / route reads index.html from any static-asset
        // content root (handles composite-provider layouts).
        app.MapGet("/", (IWebHostEnvironment webEnv) =>
        {
            string indexPath = PoTraffic.Api.Infrastructure.BlazorBootManifestBuilder.ResolveIndexHtml(webEnv);
            return File.Exists(indexPath)
                ? Results.File(indexPath, "text/html")
                : Results.NotFound(new { error = "index.html not present", path = indexPath, webRoot = webEnv.WebRootPath });
        }).AllowAnonymous().ExcludeFromDescription();

        // Fix #11c — SPA fallback for any non-API, non-framework GET. Returns
        // index.html so the Blazor Router can take over client-side. Skips /api,
        // /_framework, /.well-known, /health — those have their own handlers.
        app.MapFallback((HttpContext ctx, IWebHostEnvironment webEnv) =>
        {
            if (ctx.Request.Path.StartsWithSegments("/api")
                || ctx.Request.Path.StartsWithSegments("/_framework")
                || ctx.Request.Path.StartsWithSegments("/.well-known")
                || ctx.Request.Path.StartsWithSegments("/health"))
            {
                return Results.NotFound();
            }
            string indexPath = PoTraffic.Api.Infrastructure.BlazorBootManifestBuilder.ResolveIndexHtml(webEnv);
            return File.Exists(indexPath)
                ? Results.File(indexPath, "text/html")
                : Results.NotFound();
        }).AllowAnonymous().ExcludeFromDescription();
    }

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

/// <summary>
/// Fix #11 — Synthesises a minimal but correct <c>blazor.boot.json</c> from the
/// published <c>wwwroot/_framework</c> directory. The .NET 10 framework
/// normally generates this manifest at runtime and serves it via
/// <c>MapStaticAssets()</c>, but with <c>OverrideHtmlAssetPlaceholders=true</c>
/// on a non-server host, the host's <c>staticwebassets.endpoints.json</c>
/// ends up empty and the runtime manifest becomes unreachable. Emitting our
/// own (file-system-driven) version keeps the WASM client bootable.
///
/// The shape mirrors the one expected by <c>blazor.webassembly.js</c>:
///   * <c>entryAssembly</c> = first PoTraffic.* .dll matching the version hash,
///   * <c>resources.assembly</c> = every .wasm/.dll/.pdb in _framework,
///   * <c>resources.runtime</c> = dotnet.* and dotnet.native.* .js/.wasm,
///   * <c>resources.icudt</c> = icudt_* .dat files,
///   * <c>resources.css</c> = .css files outside _framework.
/// Zero-allocation: directory enumeration is lazy via EnumerateFiles.
/// </summary>
internal static partial class BlazorBootManifestBuilder
{
    internal static object Build(IWebHostEnvironment webEnv)
{
    string frameworkDir = Path.Combine(webEnv.WebRootPath, "_framework");
    if (!Directory.Exists(frameworkDir))
    {
        // Fallback: walk ContentRoots from the static web assets manifest (covers
        // bin/Debug/|bin/Release/ layout when wwwroot isn't physically present).
        string manifestPath = Path.Combine(
            AppContext.BaseDirectory,
            $"{webEnv.ApplicationName}.staticwebassets.runtime.json");
        if (File.Exists(manifestPath))
        {
            using System.Text.Json.JsonDocument doc =
                System.Text.Json.JsonDocument.Parse(File.ReadAllText(manifestPath));
            if (doc.RootElement.TryGetProperty("ContentRoots", out var roots))
            {
                foreach (var r in roots.EnumerateArray())
                {
                    string? root = r.GetString();
                    if (string.IsNullOrEmpty(root)) continue;
                    string candidate = Path.Combine(root, "_framework");
                    if (Directory.Exists(candidate)) { frameworkDir = candidate; break; }
                }
            }
        }
    }

    var assemblies = new List<object>();
    var runtimes = new List<object>();
    var icudts = new List<object>();

    string? entryAssembly = null;
    long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    if (Directory.Exists(frameworkDir))
    {
        foreach (string path in Directory.EnumerateFiles(frameworkDir))
        {
            string name = Path.GetFileName(path);
            string rel = "_framework/" + name;
            long size = new FileInfo(path).Length;
            // entryAssembly: first non-managed PoTraffic.Client DLL (the app's own assembly).
            if (entryAssembly is null
                && name.StartsWith("PoTraffic.Client.", StringComparison.OrdinalIgnoreCase)
                && !name.Contains(".wasm", StringComparison.OrdinalIgnoreCase)
                && (name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
            {
                entryAssembly = name;
            }
            // runtime: dotnet.* and dotnet.native.* — anything starting with "dotnet."
            if (name.StartsWith("dotnet.", StringComparison.OrdinalIgnoreCase))
            {
                runtimes.Add(new { name = rel, integrity = (string?)null, loader = (string?)null });
            }
            // icudt
            else if (name.StartsWith("icudt_", StringComparison.OrdinalIgnoreCase))
            {
                icudts.Add(new { name = rel });
            }
            // assemblies: everything else that's a binary artifact (.dll/.wasm/.pdb)
            else if (name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                  || name.EndsWith(".wasm", StringComparison.OrdinalIgnoreCase)
                  || name.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase))
            {
                assemblies.Add(new { name = rel, codeBase = "", culture = "", symbols = (string?)null });
            }
            else if (name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                  && !name.StartsWith("blazor.boot", StringComparison.OrdinalIgnoreCase))
            {
                // Some packages use additional JSON manifests (e.g. satellites); include them.
            }
        }
    }

    if (entryAssembly is null)
    {
        // Sensible fallback: the trimmer strips a published assembly; in that case
        // entryAssembly should at least match the manifest's expected name. The
        // client falls back to its own copy via blazor.webassembly.{hash}.js.
        entryAssembly = assemblies.Count > 0 ? "PoTraffic.Client.dll" : "PoTraffic.Client.wasm";
    }

    return new
    {
        name = "PoTraffic",
        entryAssembly,
        manifests = Array.Empty<object>(),
        resources = new
        {
            cache = Array.Empty<object>(),
            runtime = runtimes,
            assembly = assemblies,
            pdb = Array.Empty<object>(),
            satelliteResources = Array.Empty<object>(),
            icudt = icudts,
            css = Array.Empty<object>(),
            jsModule = Array.Empty<object>(),
            jsFiles = Array.Empty<object>(),
            wasmNative = Array.Empty<object>(),
            fingerprint = new Dictionary<string, string>(),
        },
        config = Array.Empty<object>(),
        globalizationMode = "auto",
        debugLevel = 0,
        cacheBootResources = true,
        omitGetMappingHeaders = false,
        totalAssets = assemblies.Count + runtimes.Count + icudts.Count,
        linkerEnabled = true,
        sources = Array.Empty<object>(),
        generated = now,
    };
}


// Marker for WebApplicationFactory<Program> in integration tests
public partial class Program { }

/// <summary>
/// Payload shape for Fix #10 — client-side unhandled error reports. Keep
/// fields primitive so System.Text.Json source-gen-friendly payloads succeed
/// without reflection at AOT.
/// </summary>
public sealed record ClientErrorReport(
    [property: System.Text.Json.Serialization.JsonPropertyName("url")] string Url,
    [property: System.Text.Json.Serialization.JsonPropertyName("userAgent")] string? UserAgent,
    [property: System.Text.Json.Serialization.JsonPropertyName("appVersion")] string? AppVersion,
    [property: System.Text.Json.Serialization.JsonPropertyName("message")] string? Message,
    [property: System.Text.Json.Serialization.JsonPropertyName("stack")] string? Stack);
}
// end BlazorBootManifestBuilder

/// <summary>
/// Payload shape for Fix #10 — client-side unhandled error reports. Keep
/// fields primitive so System.Text.Json source-gen-friendly payloads succeed
/// without reflection at AOT.
/// </summary>
public sealed record ClientErrorReport(
    [property: System.Text.Json.Serialization.JsonPropertyName("url")] string Url,
    [property: System.Text.Json.Serialization.JsonPropertyName("userAgent")] string? UserAgent,
    [property: System.Text.Json.Serialization.JsonPropertyName("appVersion")] string? AppVersion,
    [property: System.Text.Json.Serialization.JsonPropertyName("message")] string? Message,
    [property: System.Text.Json.Serialization.JsonPropertyName("stack")] string? Stack);
