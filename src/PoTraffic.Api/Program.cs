using Azure.Extensions.AspNetCore.Configuration.Secrets;
using Azure.Data.Tables;
using Azure.Identity;
using FluentValidation;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using PoTraffic.Api.Features.Account;
using PoTraffic.Api.Features.Admin;
using PoTraffic.Api.Features.Auth;
using PoTraffic.Api.Features.Config;
using PoTraffic.Api.Features.History;
using PoTraffic.Api.Features.Maintenance;
using PoTraffic.Api.Features.MonitoringWindows;
using PoTraffic.Api.Features.Routes;
using PoTraffic.Api.Infrastructure;
using PoTraffic.Api.Infrastructure.Logging;
using PoTraffic.Api.Infrastructure.Observability;
using PoTraffic.Api.Infrastructure.Providers;
using PoTraffic.Api.Infrastructure.Scheduling;
using PoTraffic.Api.Infrastructure.Security;
using PoTraffic.Api.Infrastructure.Storage;
using PoTraffic.Api.Infrastructure.Testing;
using Scalar.AspNetCore;
using Serilog;

// ── Serilog bootstrap (MEL-only; all app code uses ILogger<T>) ───────────────
Log.Logger = new LoggerConfiguration()
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .CreateBootstrapLogger();

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
        if (isDev)
        {
            // Chain of Responsibility — try the credential, swallow CredentialUnavailableException
            // so first-time contributors without `az login` can still `dotnet run` locally.
            try
            {
                builder.Configuration.AddAzureKeyVault(
                    new Uri(vaultUri!),
                    new DefaultAzureCredential(),
                    new PrefixKeyVaultSecretManager());
            }
            catch (Exception ex) when (
                ex is Azure.Identity.CredentialUnavailableException
                || ex is Azure.RequestFailedException
                || ex is AggregateException)
            {
                // DEV-ONLY: any Key Vault failure (no `az login`, 403/disabled vault,
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
            // Production / Staging — let exceptions propagate. Rule 6 + 13.
            builder.Configuration.AddAzureKeyVault(
                new Uri(vaultUri!),
                new DefaultAzureCredential(),
                new PrefixKeyVaultSecretManager());
        }
    }

    // ── Infrastructure extension methods (grouped by responsibility) ──────────
    builder.AddObservability();
    builder.Services.AddTableStoragePersistence();
    builder.Services.AddTableStorageServices(builder.Configuration, builder.Environment);
    builder.Services.AddBackgroundJobScheduler(builder.Environment);
    builder.Services.AddSecurityServices(builder.Configuration, builder.Environment.EnvironmentName);
    builder.Services.AddTrafficProviders(builder.Configuration, builder.Environment);

    // ── Request dispatch (validation-first, see Infrastructure/Dispatch) ─────
    builder.Services.AddDispatcher(typeof(Program).Assembly);

    // ── FluentValidation ──────────────────────────────────────────────────────
    builder.Services.AddValidatorsFromAssembly(typeof(Program).Assembly);

    // ── HTTP client resilience defaults ───────────────────────────────────────
    builder.Services.ConfigureHttpClientDefaults(http => http.AddStandardResilienceHandler());

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

    WebApplication app = builder.Build();

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
        // OpenAPI document at /openapi/v1.json; Scalar UI at /scalar/v1
        app.MapOpenApi();
        app.MapScalarApiReference();
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
    app.MapSystemEndpoints();
    app.MapDiagEndpoints();
    app.MapTestingEndpoints(app.Environment);

    // Error endpoint
    app.MapGet("/error", () => Results.Problem()).ExcludeFromDescription();

    // Health check endpoint — pings DB and external APIs, returns JSON status per dependency
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

    // ── Serve Blazor WASM fallback (non-API requests) ─────────────────────────
    app.MapStaticAssets();

    if (app.Environment.IsEnvironment("Testing"))
    {
        string testingIndexPath = Path.GetFullPath(
            Path.Combine(app.Environment.ContentRootPath, "..", "PoTraffic.Client", "wwwroot", "index.html"));

        if (File.Exists(testingIndexPath))
        {
            app.MapGet("/index.html", () => Results.File(testingIndexPath, "text/html"));
            app.MapFallback(() => Results.File(testingIndexPath, "text/html"));
        }
        else
        {
            app.MapFallbackToFile("index.html");
        }
    }
    else
    {
        app.MapFallbackToFile("index.html");
    }

    // ── Startup: hydrate the working set from Table Storage + seed configuration ──
    // Idempotent — safe to run on every cold-start. Production treats a hydration
    // failure as fatal (running without durability would silently lose user data);
    // Development/Testing degrade to memory-only so a checkout without Azurite
    // still boots (Rule 10 — First-Run Success).
    ILogger<Program> startupLog = app.Services.GetRequiredService<ILogger<Program>>();
    TableStorageContext db = app.Services.GetRequiredService<TableStorageContext>();
    try
    {
        await db.HydrateAsync();
        startupLog.LogInformation("Table Storage hydrated: {Users} users, {Routes} routes, {Polls} polls.",
            db.Users.Count(), db.Routes.Count(), db.Polls.Count());
    }
    catch (Exception ex) when (!app.Environment.IsProduction())
    {
        db.MarkVolatile();
        startupLog.LogWarning(ex,
            "Table Storage unreachable — running MEMORY-ONLY (data lost on restart). Start Azurite (docker compose up -d) to persist.");
    }

    // Seed default SystemConfiguration rows (cost rates, daily quota) and persist them.
    db.SeedDefaultConfigurationsIfMissing();
    await db.SaveChangesAsync();

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

// Marker for WebApplicationFactory<Program> in integration tests
public partial class Program { }
